using System;
using System.IO;
using System.Text;

namespace ZID
{
    // What a new row does once it is in the list.
    internal enum AddStart { Start, Paused, WaitForVideo }

    // Naming and validation rules for a new download. Duplicate prompts, ids
    // and queue/state changes stay in the form.
    internal static class AddService
    {
        internal static bool IsSupportedUrl(string url)
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

        // Copies the request fields onto a new row. The caller has already
        // resolved the cookie and picked the local path.
        internal static DownloadItem CreateItem(int id, string url, string local, string type,
                                                string cookie, string referer, string userAgent,
                                                string kind, bool auto, string pairKey,
                                                string headers, string checksum, string mirror,
                                                string proxy, string parts, string speedKbps)
        {
            DownloadItem item = new DownloadItem();
            item.Id         = id;
            item.FileName   = Path.GetFileName(local);
            item.Url        = url;
            item.Cookie     = cookie;
            item.Referer    = Util.IsBlank(referer) ? "" : referer.Trim();
            item.UserAgent  = Util.IsBlank(userAgent) ? "" : userAgent.Trim();
            item.Headers    = Util.NormalizeHeaders(headers);
            item.Checksum   = Util.NormalizeChecksum(checksum);
            item.Mirror     = Util.NormalizeMirror(mirror, url);
            item.Proxy      = Util.IsBlank(proxy) ? "" : proxy.Trim();
            item.Type       = type;
            item.Kind       = Util.IsBlank(kind) ? "" : kind.Trim().ToLowerInvariant();
            item.Auto       = auto;
            item.MergeKey   = Util.IsBlank(pairKey) ? Aria.YouTubeId(referer) : pairKey.Trim();
            item.LocalPath  = local;
            int pv;
            item.MaxConn = (int.TryParse(parts, out pv) && pv >= 1 && pv <= 16) ? pv : 0;
            long sv;
            item.SpeedKbps = (long.TryParse(speedKbps, out sv) && sv >= 0) ? sv : 0;
            return item;
        }

        // Hidden audio waits for its video; everything else starts now or stays
        // paused when the caller asked for that.
        internal static AddStart StartKind(DownloadItem item, bool startNow)
        {
            if (item.Auto && item.Kind == "audio") return AddStart.WaitForVideo;
            return startNow ? AddStart.Start : AddStart.Paused;
        }

        // Requested name wins; otherwise the URL path name, then the raw tail,
        // then "download_<id>.bin". Always a bare, safe file name.
        internal static string FileName(string url, string requested, int fallbackId)
        {
            string fileName = requested;
            if (Util.IsBlank(fileName))
            {
                try
                {
                    Uri u;
                    if (Uri.TryCreate(url, UriKind.Absolute, out u))
                    {
                        string tail = Path.GetFileName(u.AbsolutePath);
                        if (string.IsNullOrEmpty(tail)) tail = "";
                        int q = tail.IndexOf('?');
                        if (q >= 0) tail = tail.Substring(0, q);
                        if (!string.IsNullOrEmpty(tail)) fileName = tail;
                    }
                    if (Util.IsBlank(fileName))
                    {
                        string tail2 = url.Substring(url.LastIndexOf('/') + 1);
                        int q2 = tail2.IndexOf('?');
                        if (q2 >= 0) tail2 = tail2.Substring(0, q2);
                        if (string.IsNullOrEmpty(tail2)) tail2 = "download_" + fallbackId + ".bin";
                        fileName = tail2;
                    }
                }
                catch { fileName = "download_" + fallbackId + ".bin"; }
            }
            return Util.SanitizeFileName(Path.GetFileName(fileName));
        }

        internal static string TypeFor(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return string.IsNullOrEmpty(ext) ? "FILE" : ext.TrimStart('.').ToUpperInvariant();
        }

        // An explicit save folder wins; otherwise auto-sort by type. Falls back
        // to the default folder when the chosen one cannot be created.
        internal static string PrepareFolder(string downloadFolder, string saveFolder,
                                             bool autoSort, string type)
        {
            string folder = Util.IsBlank(saveFolder)
                ? (autoSort ? Path.Combine(downloadFolder, Aria.TypeFolder(type)) : downloadFolder)
                : saveFolder;
            try { Directory.CreateDirectory(folder); }
            catch { folder = downloadFolder; }
            return folder;
        }

        // Optional relative path under the download folder. {host} is the URL
        // host, {type} the type folder and {name} the file name with extension;
        // a template without {name} is a folder and keeps the original name.
        internal static string TemplateFolder(string downloadFolder, string template, string url,
                                              string type, ref string fileName)
        {
            if (Util.IsBlank(template)) return downloadFolder;
            string original = fileName;
            string host = "site";
            try
            {
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u) && !Util.IsBlank(u.Host)) host = u.Host;
            }
            catch { }
            bool named = template.IndexOf("{name}", StringComparison.Ordinal) >= 0;
            string path = template.Replace("{host}", host)
                                  .Replace("{type}", Aria.TypeFolder(type))
                                  .Replace("{name}", Path.GetFileName(fileName));
            string[] parts = path.Split(new char[] { '/', '\\' },
                                        StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            foreach (string part in parts)
            {
                if (part == "." || part == "..") continue;
                string s = Util.SanitizeFileName(part);
                if (sb.Length > 0) sb.Append('\\');
                sb.Append(s);
            }
            if (sb.Length == 0) return downloadFolder;

            string full = Path.Combine(downloadFolder, sb.ToString());
            string folder;
            if (named)
            {
                fileName = Path.GetFileName(full);
                folder = Path.GetDirectoryName(full);
            }
            else folder = full;
            try { Directory.CreateDirectory(folder); }
            catch { fileName = original; return downloadFolder; }
            return folder;
        }

        // Adds " (1)", " (2)" ... until the path is free.
        internal static string UniquePath(string folder, string fileName)
        {
            return UniquePath(folder, fileName, null);
        }

        // taken also reserves paths that a queued row already owns but that are
        // not on disk yet; otherwise two rows would write the same file.
        internal static string UniquePath(string folder, string fileName, Predicate<string> taken)
        {
            string local = Path.Combine(folder, fileName);
            int n = 1;
            while (File.Exists(local) || (taken != null && taken(local)))
            {
                string b = Path.GetFileNameWithoutExtension(fileName);
                string e = Path.GetExtension(fileName);
                local = Path.Combine(folder, b + " (" + n + ")" + e);
                n++;
            }
            return local;
        }

        // HLS/DASH always lands in a .mp4 next to the original name.
        internal static string HlsOutputPath(string folder, string fileName)
        {
            return HlsOutputPath(folder, fileName, null);
        }

        internal static string HlsOutputPath(string folder, string fileName, Predicate<string> taken)
        {
            string b = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(b)) b = "video";
            string path = Path.Combine(folder, b + ".mp4");
            int n = 1;
            while (File.Exists(path) || (taken != null && taken(path)))
            {
                path = Path.Combine(folder, b + " (" + n + ").mp4");
                n++;
            }
            return path;
        }
    }
}
