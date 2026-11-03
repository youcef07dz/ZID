using System;
using System.Collections.Generic;
using System.IO;

namespace ZID
{
    // History retention policy: which finished rows may be dropped and when the
    // user is asked. The prompt itself stays in the form.
    internal static class HistoryService
    {
        internal const int MaxItems = 5000;
        internal const int FirstPromptAt = 100;
        internal const int PromptStep = 50;
        internal const int OrphanAudioMinutes = 30;

        internal static bool IsFinished(DownloadItem item)
        {
            return DownloadStates.IsFinished(item.State);
        }

        // Hidden audio still waiting for its video must survive both trims.
        internal static bool IsPendingMerge(DownloadItem item)
        {
            return item.Kind == "audio" && !item.Merged && !Util.IsBlank(item.MergeKey);
        }

        internal static int CountFinished(DownloadManager downloads)
        {
            return downloads.CountWhere(delegate(DownloadItem it) { return IsFinished(it); });
        }

        // Drop the oldest finished rows past the cap. An old, auto-captured
        // audio row with no video to pair with is an orphan and always goes.
        internal static int Trim(DownloadManager downloads, int maxItems, DateTime now)
        {
            List<DownloadItem> snapshot = downloads.Snapshot();
            int left = CountFinished(downloads) - maxItems;
            return downloads.RemoveWhere(delegate(DownloadItem it)
            {
                bool orphan = it.Auto && it.Kind == "audio" && it.State == DownloadState.Waiting &&
                              MediaService.FindPair(snapshot, it, "video") == null &&
                              (now - it.Date).TotalMinutes >= OrphanAudioMinutes;
                bool trim = orphan || (left > 0 && IsFinished(it) && !IsPendingMerge(it));
                if (!trim) return false;
                if (!orphan) left--;
                return true;
            });
        }

        // Remove finished rows only; files stay on disk. Pending merge audio stays.
        internal static int Clean(DownloadManager downloads)
        {
            return downloads.RemoveWhere(delegate(DownloadItem it)
            {
                return IsFinished(it) && !IsPendingMerge(it);
            });
        }

        // Delete leftover .aria2/.part files under the download folder that no
        // remaining row owns. An orphan .aria2 also marks its target as a
        // partial, so that unowned file goes with it.
        internal static int RemoveOrphans(DownloadManager downloads, string folder)
        {
            if (Util.IsBlank(folder)) return 0;
            string root;
            try { root = Path.GetFullPath(folder); } catch { return 0; }
            if (!Directory.Exists(root)) return 0;

            Dictionary<string, bool> keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (DownloadItem it in downloads.Snapshot())
            {
                if (Util.IsBlank(it.LocalPath)) continue;
                string p;
                try { p = Path.GetFullPath(it.LocalPath); } catch { continue; }
                keep[p] = true;
                keep[p + ".aria2"] = true;
                keep[p + ".part"] = true;
            }

            string[] files;
            try { files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
            catch { return 0; }

            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? root : root + Path.DirectorySeparatorChar;
            int removed = 0;
            foreach (string file in files)
            {
                string full;
                try { full = Path.GetFullPath(file); } catch { continue; }
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (keep.ContainsKey(full)) continue;

                bool control = full.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase);
                bool part = full.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                if (!control && !part) continue;
                try { File.Delete(full); removed++; }
                catch { continue; }

                if (control)
                {
                    string target = full.Substring(0, full.Length - 6);
                    if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        !keep.ContainsKey(target) && File.Exists(target))
                    {
                        try { File.Delete(target); removed++; } catch { }
                    }
                }
            }
            return removed;
        }

        // Ask once at FirstPromptAt, then every PromptStep more if kept.
        internal static int NextPromptAt(int finished, bool removed)
        {
            return removed ? FirstPromptAt : finished + PromptStep;
        }
    }
}
