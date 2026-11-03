using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZID
{
    // Pure helpers shared by the app and the test harness (tests.cs).
    internal static class Aria
    {
        // "HH:MM" -> seconds since midnight, or -1 when unset/invalid.
        public static int ParseHhmm(string s)
        {
            if (s == null) return -1;
            s = s.Trim();
            if (s.Length == 0) return -1;
            string[] p = s.Split(':');
            int h, m;
            if (p.Length != 2) return -1;
            if (!int.TryParse(p[0].Trim(), out h) || !int.TryParse(p[1].Trim(), out m)) return -1;
            if (h < 0 || h > 23 || m < 0 || m > 59) return -1;
            return h * 3600 + m * 60;
        }

        private static bool InList(string ext, string list)
        {
            return ("," + list + ",").IndexOf("," + ext + ",", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Folder name used by the "sort into type folders" option.
        public static string TypeFolder(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return "Other";
            if (InList(ext, "MP4,MKV,AVI,MOV,WMV,FLV,WEBM,MPG,MPEG,M4V,TS,3GP,OGV,VTT,SRT,ASS,SSA")) return "Video";
            if (InList(ext, "JPG,JPEG,PNG,GIF,BMP,WEBP,TIFF,TIF,SVG,ICO")) return "Image";
            if (InList(ext, "MP3,WAV,FLAC,AAC,OGG,WMA,M4A,OPUS")) return "Audio";
            if (InList(ext, "ZIP,RAR,7Z,GZ,TAR,BZ2,XZ,ISO")) return "Compressed";
            if (InList(ext, "EXE,MSI,APPX,BAT,COM,APK,DMG,JAR")) return "Program";
            return "Other";
        }

        // Stable grouping key for a YouTube watch/shorts/embed URL.
        public static string YouTubeId(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                Uri u = new Uri(url);
                string host = u.Host.ToLowerInvariant();
                string id = "";

                if (host == "youtu.be")
                {
                    string p = u.AbsolutePath.Trim('/');
                    int slash = p.IndexOf('/');
                    id = slash > 0 ? p.Substring(0, slash) : p;
                }
                else if (host == "youtube.com" || host.EndsWith(".youtube.com") ||
                         host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com"))
                {
                    string q = u.Query;
                    if (q.Length > 1)
                    {
                        foreach (string part in q.Substring(1).Split('&'))
                        {
                            int eq = part.IndexOf('=');
                            if (eq > 0 && string.Equals(part.Substring(0, eq), "v",
                                                       StringComparison.OrdinalIgnoreCase))
                            {
                                id = part.Substring(eq + 1);
                                break;
                            }
                        }
                    }
                    if (id.Length == 0)
                    {
                        string p = u.AbsolutePath.Trim('/');
                        if (p.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith("embed/", StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith("live/", StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith("v/", StringComparison.OrdinalIgnoreCase))
                        {
                            id = p.Substring(p.IndexOf('/') + 1);
                            int slash = id.IndexOf('/');
                            if (slash >= 0) id = id.Substring(0, slash);
                        }
                    }
                }

                id = id.Trim();
                if (id.Length == 0) return "";
                foreach (char c in id)
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return "";
                return id;
            }
            catch { return ""; }
        }

        // "Title - 1080p60 HDR.mp4" -> "Title"; keeps names like "My - Trip".
        public static string MergeBaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string name = Path.GetFileNameWithoutExtension(path);
            int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) return name;
            string suffix = name.Substring(dash + 3);
            if (suffix.Length == 0 || suffix.Length > 24) return name;
            if (Regex.IsMatch(suffix, @"^\d{3,4}p(\d{1,3})?( (HDR|Premium))?$",
                              RegexOptions.IgnoreCase) ||
                Regex.IsMatch(suffix, @"^\d+ ?kbps$", RegexOptions.IgnoreCase) ||
                suffix.StartsWith("itag", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, dash);
            return name;
        }

        // DPAPI for secrets at rest (cookies, proxy, extra headers).
        private const string EncPrefix = "enc:";
        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(s);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return EncPrefix + Convert.ToBase64String(enc);
            }
            // Never fall back to plaintext: a secret that cannot be encrypted is dropped.
            catch { return ""; }
        }

        public static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(EncPrefix, StringComparison.Ordinal) != 0) return s;
            try
            {
                byte[] enc = Convert.FromBase64String(s.Substring(EncPrefix.Length));
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }

        // Minimal JSON reader for aria2 RPC replies: object -> Dictionary<string,object>,
        // array -> List<object>, number -> double, plus string/bool/null.
        public static object ParseJson(string s)
        {
            if (s == null) throw new FormatException("empty json");
            int i = 0;
            object v = ParseJsonValue(s, ref i);
            SkipJsonWs(s, ref i);
            if (i != s.Length) throw new FormatException("trailing json data");
            return v;
        }

        private static void SkipJsonWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static object ParseJsonValue(string s, ref int i)
        {
            SkipJsonWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of json");
            char c = s[i];
            if (c == '{') return ParseJsonObject(s, ref i);
            if (c == '[') return ParseJsonArray(s, ref i);
            if (c == '"') return ParseJsonString(s, ref i);
            if (c == 't' && i + 3 < s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 4 < s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 < s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            return ParseJsonNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseJsonObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++;
            SkipJsonWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipJsonWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected json key");
                string key = ParseJsonString(s, ref i);
                SkipJsonWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                d[key] = ParseJsonValue(s, ref i);
                SkipJsonWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end of object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        private static List<object> ParseJsonArray(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++;
            SkipJsonWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseJsonValue(s, ref i));
                SkipJsonWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end of array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        private static string ParseJsonString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 3 >= s.Length) throw new FormatException("bad unicode escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad json escape");
                }
            }
            throw new FormatException("unterminated json string");
        }

        private static double ParseJsonNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("bad json value");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture);
        }

        public static string JsonEscape(string s)        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else         sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // Simple "key=value" language files in the lang\ folder.
    internal static class Lang
    {
        public static Dictionary<string, string> Load(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l[0] == '#') continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = l.Substring(0, eq).Trim();
                    string v = l.Substring(eq + 1).Trim();
                    if (k.Length > 0) d[k] = v;
                }
            }
            catch { }
            return d;
        }

        public static string Get(Dictionary<string, string> d, string key, string fallback)
        {
            string v;
            if (d != null && d.TryGetValue(key, out v) && v.Length > 0) return v;
            return fallback != null ? fallback : key;
        }

        // English base plus the selected file, so missing keys fall back to English.
        public static Dictionary<string, string> Merge(Dictionary<string, string> baseDict,
                                                       Dictionary<string, string> selected)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (baseDict != null) foreach (KeyValuePair<string, string> kv in baseDict) d[kv.Key] = kv.Value;
            if (selected != null) foreach (KeyValuePair<string, string> kv in selected) d[kv.Key] = kv.Value;
            return d;
        }

        public static string Status(Dictionary<string, string> d, string status)
        {
            if (string.IsNullOrEmpty(status)) return "-";
            string v;
            string k = status.ToLowerInvariant();
            if (d != null && d.TryGetValue("status." + k, out v) && v.Length > 0) return v;
            if (status.StartsWith("Scheduled for ", StringComparison.Ordinal))
                return Get(d, "status.scheduled", "Scheduled for") + " " + status.Substring(14);
            if (status.StartsWith("Network error, retrying (", StringComparison.Ordinal))
            {
                int i = status.IndexOf('(');
                if (i > 0)
                    return Get(d, "status.retrying", "Network error, retrying") + " " + status.Substring(i);
            }
            return status;
        }
    }
}
