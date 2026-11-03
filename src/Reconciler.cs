using System;
using System.Collections.Generic;
using System.IO;

namespace ZID
{
    // What the one-shot startup reconciliation changed.
    internal sealed class ReconcileReport
    {
        internal int Linked;
        internal int Completed;
        internal int Failed;
        internal int Orphans;

        internal int Changed
        {
            get { return Linked + Completed + Failed + Orphans; }
        }
    }

    // Startup reconciliation table (plan section 10): persisted rows <->
    // tasks the engine still knows. Runs once, before the first worker adds
    // anything, so a task that outlived ZID is adopted or removed instead of
    // running unseen alongside a duplicate download.
    internal static class Reconciler
    {
        internal static ReconcileReport Run(List<DownloadItem> items, IDownloadEngine engine,
                                            StateLog log)
        {
            ReconcileReport report = new ReconcileReport();

            List<string> gids;
            try { gids = engine.ListGids(); }
            catch (Exception ex)
            {
                if (log != null) log("reconcile: cannot list engine tasks: " + ex.Message);
                return report;
            }
            if (gids == null) return report;

            Dictionary<string, DownloadItem> byPath =
                new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++)
            {
                DownloadItem it = items[i];
                if (it == null) continue;
                string key = PathKey(it.LocalPath);
                if (key.Length > 0 && !byPath.ContainsKey(key)) byPath[key] = it;
            }

            for (int g = 0; g < gids.Count; g++)
            {
                string gid = gids[g];
                if (Util.IsBlank(gid)) continue;

                EngineTaskState st;
                try { st = engine.Tell(gid); }
                catch (Exception ex)
                {
                    if (log != null) log("reconcile: " + gid + ": " + ex.Message);
                    continue;
                }
                if (st == null) continue;
                if (string.IsNullOrEmpty(st.Gid)) st.Gid = gid;

                DownloadItem it = Find(items, byPath, st);
                if (it == null)
                {
                    Remove(engine, gid);
                    report.Orphans++;
                    if (log != null) log("reconcile: removed orphan task " + gid);
                    continue;
                }

                // The engine finished it: trust only a verified file. This also
                // covers a row persisted as Done whose engine result survived.
                if (st.Status == EngineTaskStatus.Complete)
                {
                    if (ApplyComplete(it, st, log)) report.Completed++;
                    else report.Failed++;
                    try { engine.RemoveResult(gid); } catch { }
                    continue;
                }

                // A task for a row that is not waiting to run (Done, failed,
                // hidden audio, merge) is stale: never leave it running.
                if (!Wanted(it.State))
                {
                    Remove(engine, gid);
                    report.Orphans++;
                    if (log != null) log("reconcile: removed stale task " + gid +
                                         " for #" + it.Id + " (" + it.State + ")");
                    continue;
                }

                if (st.Status == EngineTaskStatus.Error)
                {
                    ApplyError(it, st, log);
                    report.Failed++;
                    try { engine.RemoveResult(gid); } catch { }
                    continue;
                }

                if (st.Status == EngineTaskStatus.Active ||
                    st.Status == EngineTaskStatus.Waiting ||
                    st.Status == EngineTaskStatus.Paused)
                {
                    it.EngineId = gid;
                    if (it.State == DownloadState.Paused ||
                        it.State == DownloadState.WaitingNetwork)
                    {
                        // Paused by the user or by a network wait: keep the
                        // surviving task paused so it cannot run unseen.
                        if (st.Status != EngineTaskStatus.Paused)
                        {
                            try { engine.Pause(gid); } catch { }
                        }
                        it.Eta = it.State == DownloadState.WaitingNetwork
                            ? "Waiting for network" : "Paused";
                    }
                    else
                    {
                        if (st.TotalLength > 0)
                        {
                            it.Size = st.TotalLength;
                            it.Progress = (int)(st.CompletedLength * 100 / st.TotalLength);
                        }
                        if (st.CompletedLength > 0) it.BytesReceived = st.CompletedLength;
                    }
                    report.Linked++;
                    if (log != null) log("reconcile: linked #" + it.Id + " to " + gid +
                                         " (" + st.Status + ")");
                    continue;
                }

                Remove(engine, gid);
                report.Orphans++;
                if (log != null) log("reconcile: removed unknown task " + gid +
                                     " for #" + it.Id);
            }

            return report;
        }

        // Rows that a worker or the queue is expected to drive.
        private static bool Wanted(DownloadState state)
        {
            return state == DownloadState.Queued || state == DownloadState.Starting ||
                   state == DownloadState.Downloading || state == DownloadState.Paused ||
                   state == DownloadState.WaitingNetwork;
        }

        private static DownloadItem Find(List<DownloadItem> items,
                                         Dictionary<string, DownloadItem> byPath,
                                         EngineTaskState st)
        {
            string key = PathKey(st.LocalPath);
            if (key.Length > 0)
            {
                DownloadItem hit;
                if (byPath.TryGetValue(key, out hit)) return hit;
            }

            if (!Util.IsBlank(st.Url))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    DownloadItem it = items[i];
                    if (it != null && Util.SameUrl(it.Url, st.Url)) return it;
                }
            }
            return null;
        }

        private static string PathKey(string path)
        {
            if (Util.IsBlank(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.Trim(); }
        }

        // A completed task is only trusted when the file is there and its
        // length matches the engine (checksum errors already arrive as Error).
        private static bool ApplyComplete(DownloadItem it, EngineTaskState st, StateLog log)
        {
            long actual = -1;
            try { if (File.Exists(it.LocalPath)) actual = new FileInfo(it.LocalPath).Length; }
            catch { }

            if (Util.LooksLikeWebPage(it.LocalPath, it.FileName))
            {
                it.State        = DownloadState.Failed;
                it.Eta          = "Error: server returned a web page (possible network login)";
                it.EngineId     = null;
                it.ResumeOnLoad = false;
                it.Speed        = 0;
                it.ActiveConnections = 0;
                try { File.Delete(it.LocalPath); } catch { }
                try { File.Delete(it.LocalPath + ".aria2"); } catch { }
                if (log != null) log("reconcile: #" + it.Id + " " + it.Eta);
                return false;
            }

            long expected = st.TotalLength > 0 ? st.TotalLength : it.Size;
            if (actual < 0 || (expected > 0 && actual != expected))
            {
                it.State        = DownloadState.Failed;
                it.Eta          = actual < 0
                    ? "Error: file missing after engine completion"
                    : "Error: incomplete file (" + actual + "/" + expected + " bytes)";
                it.EngineId     = null;
                it.ResumeOnLoad = false;
                it.Speed        = 0;
                it.ActiveConnections = 0;
                if (log != null) log("reconcile: #" + it.Id + " " + it.Eta);
                return false;
            }

            it.State        = DownloadState.Completed;
            it.Progress     = 100;
            it.Size         = actual;
            it.BytesReceived = actual;
            it.Speed        = 0;
            it.ActiveConnections = 0;
            it.Eta          = "Done";
            it.EngineId     = null;
            it.ResumeOnLoad = false;
            it.RetryCount   = 0;
            if (log != null) log("reconcile: #" + it.Id + " finished on the engine, file verified");
            return true;
        }

        private static void ApplyError(DownloadItem it, EngineTaskState st, StateLog log)
        {
            it.State        = DownloadState.Failed;
            it.Eta          = "Error: " + (Util.IsBlank(st.ErrorText)
                ? EngineError.Text(st.ErrorCode, st.ErrorMessage) : st.ErrorText);
            it.EngineId     = null;
            it.ResumeOnLoad = false;
            it.Speed        = 0;
            it.ActiveConnections = 0;
            if (log != null) log("reconcile: #" + it.Id + " " + it.Eta);
        }

        private static void Remove(IDownloadEngine engine, string gid)
        {
            try { engine.Remove(gid); } catch { }
        }
    }
}
