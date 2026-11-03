using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZID
{
    // Small value and file-name helpers shared by the UI, stores and tests.
    internal static class Util
    {
        internal static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        internal static string NormalizeHeaders(string s)
        {
            if (IsBlank(s)) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return s.Length > 8192 ? s.Substring(0, 8192) : s;
        }

        internal static string NormalizeChecksum(string s)
        {
            if (IsBlank(s)) return "";
            s = s.Trim();
            if (s.IndexOf('=') <= 0 || s.IndexOf(' ') >= 0) return "";
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        internal static string NormalizeMirror(string s, string primary)
        {
            if (IsBlank(s)) return "";
            s = s.Trim();
            try
            {
                Uri u;
                if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return "";
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps &&
                    u.Scheme != Uri.UriSchemeFtp) return "";
                if (string.Equals(s, primary, StringComparison.OrdinalIgnoreCase)) return "";
            }
            catch { return ""; }
            return s;
        }

        private static readonly string[] ReservedNames = new string[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // A remote filename is never trusted: no folders, no "..", no device
        // names, no trailing dots/spaces, and a bounded length that keeps the extension.
        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "download.bin";

            name = Path.GetFileName(name);
            if (name == "." || name == "..") return "download.bin";

            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++)
                name = name.Replace(bad[i], '_');

            name = name.Trim().TrimEnd(' ', '.');
            if (name.Length == 0 || name == "." || name == "..") return "download.bin";

            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    name = "_" + name;
                    break;
                }
            }

            const int MaxName = 200;
            if (name.Length > MaxName)
            {
                string ext = Path.GetExtension(name);
                if (ext.Length > 32) ext = ext.Substring(0, 32);
                int keep = MaxName - ext.Length;
                if (keep < 1) keep = 1;
                name = name.Substring(0, keep) + ext;
            }

            name = name.TrimEnd(' ', '.');
            return name.Length == 0 ? "download.bin" : name;
        }

        // Non-secret display copy: show that a value exists without revealing it.
        internal static string MaskValue(string value)
        {
            return IsBlank(value) ? "" : "********";
        }

        // http(s)/ftp URLs compared for duplicates: fragment and default port
        // ignored, host lowercase, one trailing path slash removed.
        internal static string NormalizeUrl(string url)
        {
            if (IsBlank(url)) return "";
            try
            {
                Uri u;
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "";
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps &&
                    u.Scheme != Uri.UriSchemeFtp) return "";
                string path = u.AbsolutePath;
                while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
                    path = path.Substring(0, path.Length - 1);
                if (path == "/") path = "";
                return u.Scheme.ToLowerInvariant() + "://" + u.Host.ToLowerInvariant() +
                       (u.IsDefaultPort ? "" : ":" + u.Port) + path + u.Query;
            }
            catch { return ""; }
        }

        internal static bool SameUrl(string a, string b)
        {
            string ka = NormalizeUrl(a);
            return ka.Length > 0 && ka == NormalizeUrl(b);
        }

        // Host of a URL, lowercase, for the blocked-website list.
        internal static string HostOf(string url)
        {
            if (IsBlank(url)) return "";
            try
            {
                Uri u;
                if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out u))
                    return u.Host.TrimEnd('.').ToLowerInvariant();
            }
            catch { }
            return "";
        }

        // One host or domain per line/comma; "*." and "." prefixes are accepted.
        internal static string NormalizeHosts(string list)
        {
            if (IsBlank(list)) return "";
            string[] parts = list.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                                        StringSplitOptions.RemoveEmptyEntries);
            List<string> hosts = new List<string>();
            foreach (string raw in parts)
            {
                string h = raw.Trim().TrimEnd('.').ToLowerInvariant();
                if (h.StartsWith("*.")) h = h.Substring(2);
                h = h.TrimStart('.');
                if (h.Length < 3 || h.IndexOf('/') >= 0 || h.IndexOf(':') >= 0) continue;
                if (!hosts.Contains(h)) hosts.Add(h);
            }
            return string.Join(", ", hosts.ToArray());
        }

        // A blocked domain also covers its subdomains.
        internal static bool IsBlockedHost(string list, string host)
        {
            if (IsBlank(list) || IsBlank(host)) return false;
            host = host.Trim().TrimEnd('.').ToLowerInvariant();
            string[] parts = list.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                                        StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string h = raw.Trim().TrimEnd('.').ToLowerInvariant();
                if (h.StartsWith("*.")) h = h.Substring(2);
                h = h.TrimStart('.');
                if (h.Length == 0) continue;
                if (host == h || host.EndsWith("." + h, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static string AddBlockedHost(string list, string host)
        {
            if (IsBlank(host)) return NormalizeHosts(list);
            return NormalizeHosts((IsBlank(list) ? "" : list + ", ") + host);
        }

        // The embedded page is the only trusted caller of window.external, so
        // only the app document (about:blank from DocumentText) and IE's res:
        // helpers may load. A dropped file or followed link must not replace it.
        internal static bool AllowDocumentNavigation(string url)
        {
            if (IsBlank(url)) return true;
            try
            {
                Uri u;
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return false;
                return u.Scheme == "about" || u.Scheme == "res";
            }
            catch { return false; }
        }

        internal static string FormatEta(long seconds)
        {
            return string.Format("{0:00}:{1:00}:{2:00}",
                                 seconds / 3600, (seconds / 60) % 60, seconds % 60);
        }

        // A captive portal answers 200 with a login page, so aria2 reports a
        // finished "download" that is really HTML. Web-page names are left
        // alone; everything else that starts like a page is rejected.
        internal static bool LooksLikeWebPage(string path, string fileName)
        {
            string ext = "";
            try { ext = Path.GetExtension(fileName == null ? "" : fileName).ToLowerInvariant(); }
            catch { }
            if (ext == ".html" || ext == ".htm" || ext == ".xhtml" || ext == ".shtml" ||
                ext == ".mht" || ext == ".mhtml") return false;

            byte[] head;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite))
                {
                    if (fs.Length == 0) return false;
                    head = new byte[(int)Math.Min(512, fs.Length)];
                    int n = fs.Read(head, 0, head.Length);
                    if (n <= 0) return false;
                    if (n != head.Length) Array.Resize(ref head, n);
                }
            }
            catch { return false; }

            int i = 0;
            Encoding enc = Encoding.ASCII;
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                i = 3;
            }
            else if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                i = 2;
                enc = Encoding.Unicode;
            }
            else if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                i = 2;
                enc = Encoding.BigEndianUnicode;
            }
            string text = enc.GetString(head, i, head.Length - i)
                             .TrimStart(' ', '\t', '\r', '\n', '\0')
                             .ToLowerInvariant();
            return text.StartsWith("<!doctype html") || text.StartsWith("<html") ||
                   text.StartsWith("<head") || text.StartsWith("<body");
        }

        // Unknown total size (0) never blocks a download. Free 0 is treated as
        // "unknown" by the caller today, so it keeps failing known-size rows.
        internal static bool NeedsMoreSpace(long totalSize, long received, long freeBytes)
        {
            return totalSize > 0 && totalSize - received > freeBytes;
        }

        internal static string Csv(string s)
        {
            if (s == null) s = "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
