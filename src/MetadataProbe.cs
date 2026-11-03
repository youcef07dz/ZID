using System;
using System.Net;

namespace ZID
{
    // One short HEAD request for the Add dialog. MainForm runs this on a
    // background thread and pushes the result back, so the UI never waits.
    internal static class MetadataProbe
    {
        internal const int TimeoutMs = 5000;

        // Returns {"fileName":"...","size":N} or "" when the server does not
        // answer a HEAD request in time. The caller treats "" as "no hint".
        internal static string Probe(string url)
        {
            if (!AddService.IsSupportedUrl(url)) return "";
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return "";
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return "";

            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = 5;
                req.UserAgent = "ZID";
                req.KeepAlive = false;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    long size = resp.ContentLength;
                    if (size < 0) size = 0;
                    string name = FileNameFromDisposition(resp.Headers["Content-Disposition"]);
                    return "{\"fileName\":\"" + Aria.JsonEscape(name) +
                           "\",\"size\":" + size + "}";
                }
            }
            catch
            {
                // HEAD is only a hint; a server that refuses it skips the probe.
                try { if (req != null) req.Abort(); } catch { }
                return "";
            }
        }

        // Content-Disposition: attachment; filename="a.zip"; filename*=UTF-8''a.zip
        internal static string FileNameFromDisposition(string value)
        {
            if (Util.IsBlank(value)) return "";
            string name = "";
            int star = value.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
            if (star >= 0) name = StarValue(value, star + 10);
            if (name.Length == 0)
            {
                int plain = value.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
                if (plain >= 0) name = PlainValue(value, plain + 9);
            }
            return name.Length == 0 ? "" : Util.SanitizeFileName(name);
        }

        private static string PlainValue(string s, int start)
        {
            while (start < s.Length && s[start] == ' ') start++;
            if (start >= s.Length) return "";
            if (s[start] == '"' || s[start] == '\'')
            {
                char quote = s[start++];
                int end = s.IndexOf(quote, start);
                return end < 0 ? s.Substring(start) : s.Substring(start, end - start);
            }
            int semi = s.IndexOf(';', start);
            return (semi < 0 ? s.Substring(start) : s.Substring(start, semi - start)).Trim();
        }

        // RFC 5987: charset'lang'percent-encoded
        private static string StarValue(string s, int start)
        {
            string raw = PlainValue(s, start);
            int q1 = raw.IndexOf('\'');
            int q2 = q1 < 0 ? -1 : raw.IndexOf('\'', q1 + 1);
            if (q1 < 0 || q2 < 0) return "";
            string charset = raw.Substring(0, q1).Trim().ToLowerInvariant();
            if (charset != "utf-8" && charset != "iso-8859-1") return "";
            try { return Uri.UnescapeDataString(raw.Substring(q2 + 1)); }
            catch { return ""; }
        }
    }
}
