using System;
using System.Collections.Generic;
using System.IO;

namespace ZID
{
    // Startup validation for persisted downloads. Keeps recovery decisions out
    // of the form and testable without a UI or aria2.
    internal static class Recovery
    {
        // Returns the next id after repairing/filling missing ids.
        internal static int Validate(List<DownloadItem> items, string defaultFolder,
                                     int nextId, StateLog log)
        {
            if (items == null) return nextId;
            int maxId = nextId - 1;

            for (int i = 0; i < items.Count; i++)
            {
                DownloadItem it = items[i];
                if (it == null)
                {
                    items.RemoveAt(i);
                    i--;
                    if (log != null) log("recovery: null item removed");
                    continue;
                }

                if (it.Id <= 0)
                {
                    it.Id = ++maxId;
                    if (log != null) log("recovery: missing id assigned #" + it.Id);
                }
                else if (it.Id > maxId) maxId = it.Id;

                if (Util.IsBlank(it.FileName))
                {
                    it.FileName = "download_" + it.Id + ".bin";
                    if (log != null) log("recovery: missing filename for #" + it.Id);
                }
                it.FileName = Util.SanitizeFileName(it.FileName);

                if (Util.IsBlank(it.LocalPath))
                {
                    it.LocalPath = Path.Combine(defaultFolder, it.FileName);
                    if (log != null) log("recovery: missing path for #" + it.Id);
                }

                if (!ValidUrl(it.Url))
                {
                    it.State        = DownloadState.Failed;
                    it.Eta          = "Error: invalid URL";
                    it.ResumeOnLoad = false;
                    if (log != null) log("recovery: invalid URL for #" + it.Id);
                    continue;
                }

                // A crash cannot run the graceful-exit hook that sets this, so
                // any row that was running/queued continues on the next start.
                if (it.State == DownloadState.Starting || it.State == DownloadState.Downloading ||
                    it.State == DownloadState.Queued || it.State == DownloadState.WaitingNetwork)
                {
                    it.ResumeOnLoad = true;
                }

                if (it.State == DownloadState.Completed)
                    MarkMissing(it, log);
            }

            return maxId + 1;
        }

        internal const string MissingText = "File moved or deleted";

        // Completed row whose file is gone (or came back). Returns true when Eta
        // changed, so the caller can push a fresh snapshot.
        internal static bool MarkMissing(DownloadItem it, StateLog log)
        {
            if (it == null || it.State != DownloadState.Completed) return false;
            bool exists = false;
            try { exists = File.Exists(it.LocalPath); } catch { }

            if (exists)
            {
                if (it.Eta != MissingText) return false;
                it.Eta = "Done";
                if (log != null) log("completed file restored for #" + it.Id);
                return true;
            }

            // Never run after-done actions on a file that is not there.
            it.AfterDoneRan = true;
            if (it.Eta == MissingText) return false;
            it.Eta = MissingText;
            if (log != null) log("completed file missing for #" + it.Id);
            return true;
        }

        private static bool ValidUrl(string url)
        {
            if (Util.IsBlank(url)) return false;
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
                return u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps ||
                       u.Scheme == Uri.UriSchemeFtp;
            }
            catch { return false; }
        }
    }
}
