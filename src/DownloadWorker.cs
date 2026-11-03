using System;
using System.IO;
using System.Threading;

namespace ZID
{
    // Everything the download worker needs from the form. The worker owns the
    // RPC loop, retries and playlist flow; the host owns state persistence, UI
    // updates, the queue and the media callbacks.
    internal interface IWorkerHost
    {
        DownloadManager Downloads { get; }
        QueueManager Queue { get; }
        string AriaExe { get; }
        string ToolsDir { get; }
        string DownloadFolder { get; }
        int PartsPerDownload { get; }
        long MaxSpeedKbps { get; }
        string Proxy { get; }
        IDownloadEngine Engine { get; }
        string EngineError { get; }
        bool EnsureEngine();
        long FreeSpaceFor(string path);
        void ForgetGid(DownloadItem item);
        bool CheckVideoAudio(DownloadItem video);
        bool AskCredentials(DownloadItem item);
        void TryMerge(DownloadItem item);
        void SaveState();
        void SaveStateAndFlush();
        void PushState();
        void TryStartNext();
        void Log(string message);
        void ReportCrash(string context, Exception ex);
    }

    internal sealed class DownloadWorker
    {
        private readonly IWorkerHost _host;

        internal DownloadWorker(IWorkerHost host)
        {
            _host = host;
        }

        internal void Run(DownloadItem item, int gen)
        {
            if (item.Kind == "hls" || item.Kind == "dash")
            {
                RunPlaylist(item, gen);
                return;
            }
            if (!_host.EnsureEngine())
            {
                _host.Log("aria2 unavailable: " + _host.EngineError);
                if (gen == item.Generation)
                {
                    _host.Downloads.Fail(item);
                    item.Eta          = "Error: download engine unavailable";
                    item.ResumeOnLoad = false;
                }
                item.Speed             = 0;
                item.ActiveConnections = 0;
                _host.Queue.Release();
                _host.TryStartNext();
                _host.SaveStateAndFlush();
                _host.PushState();
                return;
            }

            // Reconciliation may have finished or failed this row before the
            // worker touched the daemon; do not add it again.
            if (item.State == DownloadState.Completed || item.State == DownloadState.Failed)
            {
                item.Speed = 0;
                item.ActiveConnections = 0;
                _host.Queue.Release();
                _host.TryStartNext();
                _host.SaveStateAndFlush();
                _host.PushState();
                return;
            }
            RunRpc(item, gen, 0);
        }

        // reconnect counts daemon recoveries for this worker, so a daemon that
        // stays up but keeps failing cannot recurse forever.
        private void RunRpc(DownloadItem item, int gen, int reconnect)
        {
            string gid = null;
            bool noSpace = false;
            bool restarted = false;
            bool reconnected = false;
            bool engineDown = false;

            try
            {
                if (!File.Exists(_host.AriaExe))
                    throw new Exception("aria2c.exe not found in " + _host.ToolsDir);

                if (Util.NeedsMoreSpace(item.Size, item.BytesReceived,
                                        _host.FreeSpaceFor(item.LocalPath)))
                {
                    _host.Downloads.Fail(item);
                    item.Eta    = "Error: not enough disk space";
                    return;
                }

                if (item.Abort || gen != item.Generation) return;

                // Continue a paused daemon download instead of adding it twice.
                if (!Util.IsBlank(item.EngineId))
                {
                    try { _host.Engine.Unpause(item.EngineId); gid = item.EngineId; }
                    catch { gid = null; item.EngineId = null; }
                }
                if (gid == null)
                {
                    gid = _host.Engine.Add(item, _host.DownloadFolder, _host.PartsPerDownload,
                                           _host.MaxSpeedKbps, _host.Proxy);
                    item.EngineId = gid;
                }
                _host.Downloads.SetState(item, DownloadState.Downloading);
                item.Eta    = "Starting...";

                while (true)
                {
                    if (item.Abort || item.Skip || gen != item.Generation)
                    {
                        try { _host.Engine.Pause(gid); } catch { }
                        _host.Downloads.Pause(item);
                        item.Eta    = "Paused";
                        item.Speed  = 0;
                        item.ActiveConnections = 0;
                        break;
                    }

                    EngineTaskState st = _host.Engine.Tell(gid);
                    UpdateRpcProgress(item, st.TotalLength, st.CompletedLength,
                                      st.DownloadSpeed, st.Connections);

                    if (!noSpace &&
                        Util.NeedsMoreSpace(item.Size, st.CompletedLength,
                                            _host.FreeSpaceFor(item.LocalPath)))
                    {
                        noSpace = true;
                        try { _host.Engine.Pause(gid); } catch { }
                    }
                    if (noSpace)
                    {
                        _host.Downloads.Fail(item);
                        item.Eta          = "Error: not enough disk space";
                        item.Speed        = 0;
                        item.ActiveConnections = 0;
                        item.ResumeOnLoad = false;
                        _host.ForgetGid(item);
                        break;
                    }

                    if (st.Status == EngineTaskStatus.Complete)
                    {
                        if (RejectWebPage(item))
                        {
                            _host.Engine.RemoveResult(gid);
                            break;
                        }
                        try
                        {
                            long len = new FileInfo(item.LocalPath).Length;
                            if (len > 0) { item.Size = len; item.BytesReceived = len; }
                        }
                        catch { }
                        item.Progress = 100;
                        item.Speed    = 0;
                        item.ActiveConnections = 0;
                        if (item.Kind == "video" && _host.CheckVideoAudio(item))
                        {
                            _host.Downloads.Merge(item);
                            item.Eta      = "Waiting for audio...";
                            item.Progress = 99;
                        }
                        else
                        {
                            _host.Downloads.Complete(item);
                            item.Eta    = "Done";
                        }
                        item.ResumeOnLoad = false;
                        item.EngineId = null;
                        _host.Engine.RemoveResult(gid);
                        _host.TryMerge(item);
                        item.RetryCount = 0;
                        break;
                    }
                    if (st.Status == EngineTaskStatus.Error)
                    {
                        int code = st.ErrorCode;
                        // Server ignored Range: throw the partial file away once.
                        if (code == 8 && !restarted)
                        {
                            restarted = true;
                            _host.ForgetGid(item);
                            try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                            try { if (File.Exists(item.LocalPath + ".aria2")) File.Delete(item.LocalPath + ".aria2"); } catch { }
                            item.Size = 0;
                            item.BytesReceived = 0;
                            item.Progress = 0;
                            item.Speed = 0;
                            item.Eta = "Server cannot resume; restarting...";
                            gid = _host.Engine.Add(item, _host.DownloadFolder, _host.PartsPerDownload,
                                                   _host.MaxSpeedKbps, _host.Proxy);
                            item.EngineId = gid;
                            continue;
                        }

                        // 401/407: ask once, then retry with the new credentials.
                        // A wrong password falls through to the normal failure path.
                        if (EngineError.IsAuth(code, st.ErrorMessage) && !item.AuthPrompted)
                        {
                            item.AuthPrompted = true;
                            item.AuthProxy = st.ErrorMessage != null &&
                                st.ErrorMessage.IndexOf("407", StringComparison.Ordinal) >= 0;
                            if (_host.AskCredentials(item))
                            {
                                _host.ForgetGid(item);
                                _host.Downloads.Start(item);
                                item.RetryCount = 0;
                                item.Eta = "Starting...";
                                gid = _host.Engine.Add(item, _host.DownloadFolder,
                                                       _host.PartsPerDownload,
                                                       _host.MaxSpeedKbps, _host.Proxy);
                                item.EngineId = gid;
                                _host.SaveStateAndFlush();
                                _host.PushState();
                                continue;
                            }
                        }

                        EngineErrorClass cls = EngineError.Classify(code, st.ErrorMessage);
                        RetryDecision decision = RetryPolicy.Decide(cls, item.RetryCount,
                                                                  NetworkMonitor.IsAvailable());
                        // T1.5: a lost connection is not a download failure and
                        // must not consume the retry budget. Keep the gid paused
                        // so the restore path can unpause the same task.
                        if (decision == RetryDecision.WaitNetwork)
                        {
                            try { _host.Engine.Pause(gid); } catch { }
                            _host.Downloads.WaitNetwork(item);
                            item.Eta          = "Waiting for network";
                            item.Speed        = 0;
                            item.ActiveConnections = 0;
                            item.ResumeOnLoad = true;
                            _host.SaveState();
                            _host.PushState();
                            break;
                        }

                        if (decision == RetryDecision.Retry)
                        {
                            item.RetryCount++;
                            int delay = RetryPolicy.DelayMs(item.RetryCount, st.ErrorMessage);
                            _host.ForgetGid(item);
                            _host.Downloads.Start(item);
                            item.Eta   = "Retrying in " + (delay / 1000) + "s (" +
                                         item.RetryCount + "/" + RetryPolicy.MaxRetries + ")";
                            item.Speed = 0;
                            item.ActiveConnections = 0;
                            _host.SaveState();
                            _host.PushState();
                            if (!WaitForRetry(item, gen, delay))
                            {
                                if (gen == item.Generation)
                                {
                                    _host.Downloads.Pause(item);
                                    item.Eta   = "Paused";
                                }
                                break;
                            }
                            gid = _host.Engine.Add(item, _host.DownloadFolder, _host.PartsPerDownload,
                                                   _host.MaxSpeedKbps, _host.Proxy);
                            item.EngineId = gid;
                            continue;
                        }

                        item.Speed = 0;
                        item.ActiveConnections = 0;
                        _host.Downloads.Fail(item);
                        item.Eta    = "Error: " + st.ErrorText;
                        item.ResumeOnLoad = false;
                        item.EngineId = null;
                        _host.Engine.RemoveResult(gid);
                        break;
                    }
                    if (WorkerState.Apply(item, _host.Downloads, st.Status)) break;

                    Thread.Sleep(500);
                }
            }
            catch (Exception ex)
            {
                _host.Log("download rpc '" + item.FileName + "': " + ex.Message);
                _host.ReportCrash("download rpc " + item.FileName, ex);

                // Daemon trouble: restart (max 3 in 60 s) and re-add from the
                // partial file. The recursion depth is bounded by `reconnect`.
                if (gen == item.Generation && !item.Abort && !item.Skip && reconnect < 3)
                {
                    if (_host.Engine.Recover())
                    {
                        _host.ForgetGid(item);
                        _host.Downloads.Start(item);
                        item.Eta   = "Reconnecting to download engine...";
                        item.Speed = 0;
                        item.ActiveConnections = 0;
                        _host.SaveState();
                        _host.PushState();
                        if (!WaitForRetry(item, gen, 1000 * (reconnect + 1)))
                        {
                            if (gen == item.Generation)
                            {
                                _host.Downloads.Pause(item);
                                item.Eta   = "Paused";
                            }
                            return;
                        }
                        reconnected = true;
                        RunRpc(item, gen, reconnect + 1);
                        return;
                    }
                    engineDown = true;
                }

                if (gen == item.Generation)
                {
                    item.ActiveConnections = 0;
                    _host.Downloads.SetState(item, item.Abort ? DownloadState.Paused
                                                              : DownloadState.Failed);
                    item.Eta    = item.Abort ? "Paused" :
                        (engineDown ? "Error: download engine unavailable"
                                    : "Error: " + EngineError.UserText(ex.Message));
                    item.Speed  = 0;
                }
            }
            finally
            {
                if (!reconnected)
                {
                    if (item.Skip)
                    {
                        _host.ForgetGid(item);
                        try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                        try { if (File.Exists(item.LocalPath + ".aria2")) File.Delete(item.LocalPath + ".aria2"); } catch { }
                    }
                    item.Proc = null;
                    if (item.Auto && item.Kind == "audio" && item.State == DownloadState.Failed)
                    {
                        DownloadItem video = MediaService.FindPair(
                            _host.Downloads.Snapshot(), item, "video");
                        if (video != null)
                        {
                            _host.Downloads.Fail(video);
                            video.Eta = "Audio failed: " + item.Eta;
                        }
                    }
                    _host.Queue.Release();
                    _host.TryStartNext();
                    _host.SaveStateAndFlush();
                    _host.PushState();
                }
            }
        }

        // Sleeps in short slices so pause/skip/new-generation stays responsive.
        private static bool WaitForRetry(DownloadItem item, int gen, int ms)
        {
            for (int w = 0; w < ms; w += 200)
            {
                if (item.Abort || item.Skip || gen != item.Generation) return false;
                Thread.Sleep(200);
            }
            return !(item.Abort || item.Skip || gen != item.Generation);
        }

        private void RunPlaylist(DownloadItem item, int gen)
        {
            try
            {
                if (Util.NeedsMoreSpace(item.Size, item.BytesReceived,
                                        _host.FreeSpaceFor(item.LocalPath)))
                {
                    _host.Downloads.Fail(item);
                    item.Eta    = "Error: not enough disk space";
                    return;
                }

                IDownloadEngine rpc = null;
                if (_host.EnsureEngine()) rpc = _host.Engine;
                else _host.Log("playlist: RPC unavailable, using HTTP (" + _host.EngineError + ")");

                // Reconciliation may already have settled this row.
                if (item.State == DownloadState.Completed || item.State == DownloadState.Failed)
                {
                    _host.Queue.Release();
                    _host.TryStartNext();
                    _host.SaveStateAndFlush();
                    _host.PushState();
                    return;
                }

                _host.Downloads.SetState(item, DownloadState.Downloading);
                item.Eta    = "Reading playlist...";
                string error;
                bool ok = Playlist.Download(item, gen, _host.DownloadFolder, rpc,
                                            _host.Proxy, out error);

                if (ok && RejectWebPage(item))
                {
                    // RejectWebPage already failed the row; no success state.
                }
                else if (ok)
                {
                    _host.Downloads.Complete(item);
                    item.Eta    = "Done";
                    item.Progress = 100;
                    item.Speed  = 0;
                    item.ResumeOnLoad = false;
                }
                else if (item.Abort || gen != item.Generation)
                {
                    _host.Downloads.Pause(item);
                    item.Eta    = "Paused";
                    item.Speed  = 0;
                }
                else
                {
                    _host.Downloads.Fail(item);
                    item.Eta    = "Error: " + (Util.IsBlank(error) ? "playlist download failed" : error);
                    item.ResumeOnLoad = false;
                }
            }
            catch (Exception ex)
            {
                _host.Log("playlist '" + item.FileName + "': " + ex.Message);
                _host.ReportCrash("playlist " + item.FileName, ex);
                if (gen == item.Generation)
                {
                    item.ActiveConnections = 0;
                    _host.Downloads.SetState(item, item.Abort ? DownloadState.Paused
                                                              : DownloadState.Failed);
                    item.Eta    = item.Abort ? "Paused" :
                                  "Error: " + EngineError.UserText(ex.Message);
                    item.Speed  = 0;
                }
            }
            finally
            {
                if (item.Skip)
                {
                    try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                    try { if (Directory.Exists(item.LocalPath + ".zidseg")) Directory.Delete(item.LocalPath + ".zidseg", true); } catch { }
                }
                item.Proc = null;
                _host.Queue.Release();
                _host.TryStartNext();
                _host.SaveStateAndFlush();
                _host.PushState();
            }
        }

        // A captive portal answers 200 with a login page, so the engine reports
        // a finished "download" that is really HTML. Drop the body (it is not
        // the requested file and would break resume) and fail the row.
        private bool RejectWebPage(DownloadItem item)
        {
            if (!Util.LooksLikeWebPage(item.LocalPath, item.FileName)) return false;
            _host.Log("content: " + item.FileName + " is a web page, not the requested file");
            try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
            try { if (File.Exists(item.LocalPath + ".aria2")) File.Delete(item.LocalPath + ".aria2"); } catch { }
            item.Size = 0;
            item.BytesReceived = 0;
            item.Progress = 0;
            item.Speed = 0;
            item.ActiveConnections = 0;
            item.ResumeOnLoad = false;
            item.EngineId = null;
            _host.Downloads.Fail(item);
            item.Eta = "Error: server returned a web page (possible network login)";
            return true;
        }

        private void UpdateRpcProgress(DownloadItem item, long total, long done,
                                       long speed, int connections)
        {
            ProgressPolicy.Update(item, total, done, speed, connections);

            if (item.Auto && item.Kind == "audio")
            {
                DownloadItem video = MediaService.FindPair(
                    _host.Downloads.Snapshot(), item, "video");
                if (video != null)
                {
                    _host.Downloads.Merge(video);
                    video.Progress = item.Progress;
                    video.Eta      = item.Progress < 100 ? "Audio " + item.Progress + "%" : "Muxing...";
                }
            }
        }
    }
}
