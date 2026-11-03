using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ZID
{
    // =====================================================================
    //  Update check against version.json on the project page.
    // =====================================================================
    // The file is published with every release:
    //   { "version": "1.0", "setup": "setup_1.0.exe", ... }
    // so the app never has to guess a download name.
    internal static class UpdateChecker
    {
        internal const string VersionUrl =
            "https://raw.githubusercontent.com/youcef07dz/ZID/main/version.json";

        // raw.githubusercontent.com is blocked on some networks. GitHub's
        // /raw/ page only redirects back to the blocked host, so use a mirror
        // that serves the same file from its own host.
        internal const string VersionUrlFallback =
            "https://cdn.jsdelivr.net/gh/youcef07dz/ZID@main/version.json";

        internal const string DistBase =
            "https://github.com/youcef07dz/ZID/raw/main/dist/";

        internal const int TimeoutMs = 8000;

        internal sealed class UpdateInfo
        {
            internal string Version = "";
            internal string Setup = "";
            internal string Notes = "";
            internal bool IsNewer;
        }

        // Throws on a network or HTTP failure; the caller decides whether that
        // is worth telling the user about. Both sources get their own timeout.
        internal static UpdateInfo Check(string localVersion)
        {
            UpdateInfo info = null;
            Exception last = null;
            string[] sources = new string[] { VersionUrl, VersionUrlFallback };
            for (int i = 0; i < sources.Length && info == null; i++)
            {
                try { info = Parse(Download(sources[i])); }
                catch (Exception ex) { last = ex; }
            }
            if (info == null)
                throw new Exception(last == null ? "version.json is not readable" : last.Message);
            info.IsNewer = IsNewer(info.Version, localVersion);
            return info;
        }

        internal static string SetupUrl(UpdateInfo info)
        {
            return DistBase + info.Setup;
        }

        // "1.0" equals "1.0.0.0"; "1.0.1" is newer; a suffix like "-beta" is ignored.
        internal static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "").Split('.');
            string[] pb = (b ?? "").Split('.');
            int n = pa.Length > pb.Length ? pa.Length : pb.Length;
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? Part(pa[i]) : 0;
                int y = i < pb.Length ? Part(pb[i]) : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        internal static bool IsNewer(string remote, string local)
        {
            return CompareVersions(remote, local) > 0;
        }

        private static int Part(string s)
        {
            int v = 0, digits = 0;
            for (int i = 0; i < s.Length && digits < 9; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') break;
                v = v * 10 + (c - '0');
                digits++;
            }
            return v;
        }

        // Minimal read of the two string fields; the file is written by us and
        // only these keys matter.
        internal static UpdateInfo Parse(string json)
        {
            if (Util.IsBlank(json)) return null;
            string version = JsonString(json, "version");
            if (version.Length == 0) return null;
            UpdateInfo info = new UpdateInfo();
            info.Version = version;
            info.Setup = SafeSetupName(JsonString(json, "setup"), version);
            info.Notes = Notes(json);
            return info;
        }

        // version.json may carry a plain "notes" string or the older
        // "whats_new" array; show whichever exists as short bullets.
        private static string Notes(string json)
        {
            try
            {
                Dictionary<string, object> d = Aria.ParseJson(json) as Dictionary<string, object>;
                if (d == null) return "";
                object value;
                if (d.TryGetValue("notes", out value))
                {
                    string s = value as string;
                    if (!string.IsNullOrEmpty(s)) return Limit(s.Trim());
                }
                if (d.TryGetValue("whats_new", out value))
                {
                    List<object> list = value as List<object>;
                    if (list == null) return "";
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < list.Count && sb.Length < 1200; i++)
                    {
                        string s = list[i] as string;
                        if (string.IsNullOrEmpty(s)) continue;
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append("- ").Append(s.Trim());
                    }
                    return Limit(sb.ToString());
                }
            }
            catch { }
            return "";
        }

        private static string Limit(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Trim();
            return s.Length <= 1500 ? s : s.Substring(0, 1500);
        }

        // The name comes from the network, so it may only be a plain file name.
        internal static string SafeSetupName(string name, string version)
        {
            string fallback = "setup_" + (Util.IsBlank(version) ? "latest" : version) + ".exe";
            if (Util.IsBlank(name)) return fallback;
            string trimmed = name.Trim();
            if (trimmed != Path.GetFileName(trimmed)) return fallback;
            if (trimmed.Length == 0 || trimmed.Length > 80) return fallback;
            if (trimmed.IndexOf("..", StringComparison.Ordinal) >= 0) return fallback;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok) return fallback;
            }
            if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return fallback;
            return trimmed;
        }

        private static string JsonString(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string Download(string url)
        {
            EnableTls12();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 5;
            req.UserAgent = "ZID";
            req.KeepAlive = false;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                char[] buffer = new char[65536];
                int n = reader.Read(buffer, 0, buffer.Length);
                return n <= 0 ? "" : new string(buffer, 0, n);
            }
        }

        // .NET 2.0 has no SecurityProtocol property, and Windows 7 defaults to
        // TLS 1.0, which the host no longer accepts. Set it when the runtime
        // running us exposes it.
        private static void EnableTls12()
        {
            try
            {
                PropertyInfo p = typeof(ServicePointManager).GetProperty("SecurityProtocol");
                if (p != null) p.SetValue(null, Enum.ToObject(p.PropertyType, 3072), null);
            }
            catch { }
        }
    }
}
