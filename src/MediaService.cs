using System;
using System.Collections.Generic;
using System.IO;

namespace ZID
{
    internal enum MergePairResult
    {
        Ready,
        Missing,
        AmbiguousVideo,
        AmbiguousAudio
    }

    internal delegate bool MuxFiles(string videoPath, string audioPath, string outPath, out string error);
    internal delegate void MediaLog(string message);

    internal sealed class MergeFileResult
    {
        internal bool Ok;
        internal bool SaveFailed;
        internal string Error = "";
    }

    // Pairing, naming and the file-side merge. State and UI updates stay in
    // the form so this class remains testable.
    internal static class MediaService
    {
        // Exactly one unmerged row of that kind with the same key, or null.
        internal static DownloadItem FindPair(List<DownloadItem> snapshot, DownloadItem item, string kind)
        {
            if (item == null) return null;
            DownloadItem found = null;
            for (int i = 0; i < snapshot.Count; i++)
            {
                DownloadItem o = snapshot[i];
                if (o == item || o.Merged || o.Kind != kind || o.MergeKey != item.MergeKey) continue;
                if (found != null) return null;
                found = o;
            }
            return found;
        }

        // Both rows must be finished (the video may already show Merging) and the
        // item passed in must be one of the two.
        internal static MergePairResult FindMergePair(List<DownloadItem> snapshot, DownloadItem item,
                                                     out DownloadItem video, out DownloadItem audio)
        {
            video = null;
            audio = null;
            for (int i = 0; i < snapshot.Count; i++)
            {
                DownloadItem o = snapshot[i];
                if (o.Merged || o.MergeKey != item.MergeKey) continue;
                if (o.Kind == "video")
                {
                    if (o.State != DownloadState.Completed && o.State != DownloadState.Merging) continue;
                    if (video != null) return MergePairResult.AmbiguousVideo;
                    video = o;
                }
                else if (o.Kind == "audio")
                {
                    if (o.State != DownloadState.Completed) continue;
                    if (audio != null) return MergePairResult.AmbiguousAudio;
                    audio = o;
                }
            }
            if (video == null || audio == null) return MergePairResult.Missing;
            if (video != item && audio != item) return MergePairResult.Missing;
            return MergePairResult.Ready;
        }

        internal static bool IsMp4(string path)
        {
            return EndsWith(path, ".mp4");
        }

        internal static bool IsM4a(string path)
        {
            return EndsWith(path, ".m4a");
        }

        // "Title - 1080p.mp4" -> "Title.mp4"; collision gets " (1)".
        internal static string MergeOutputPath(string videoPath)
        {
            string folder = Path.GetDirectoryName(videoPath);
            string name = Aria.MergeBaseName(videoPath);

            string path = Path.Combine(folder, name + ".mp4");
            int n = 1;
            while (File.Exists(path) &&
                   !string.Equals(path, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(folder, name + " (" + n + ").mp4");
                n++;
            }
            return path;
        }

        // Runs the muxer and installs the result. Returns a failure message when
        // the mux or the final rename failed; the temp file is cleaned up then.
        internal static MergeFileResult MergeFiles(string videoPath, string audioPath, string outPath,
                                                   MuxFiles mux, MediaLog log)
        {
            MergeFileResult r = new MergeFileResult();
            string tmp = outPath + ".tmp.mp4";
            try
            {
                if (!File.Exists(videoPath)) throw new Exception("video file missing");
                if (!File.Exists(audioPath)) throw new Exception("audio file missing");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

                string error = "";
                if (!mux(videoPath, audioPath, tmp, out error)) throw new Exception(error);
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                    throw new Exception("muxer produced no output");
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }

            try
            {
                if (string.Equals(outPath, videoPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Replace(tmp, outPath, null);
                }
                else
                {
                    File.Move(tmp, outPath);
                    try
                    {
                        if (File.Exists(videoPath)) File.Delete(videoPath);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("merge cleanup '" + Path.GetFileName(videoPath) + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                r.SaveFailed = true;
                r.Error = ex.Message;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return r;
            }

            r.Ok = true;
            return r;
        }

        private static bool EndsWith(string s, string suffix)
        {
            return s != null && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
