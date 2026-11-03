using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZID
{
    // Persisted options. Runtime-only flags (engine handles, prompts, ticker
    // timestamps) stay in the form.
    internal sealed class AppSettings
    {
        internal int ConfigVersion = 1;
        internal string Theme = "light";
        internal int MaxConcurrent = 2;
        internal int PartsPerDownload = 16;
        internal long MaxSpeedKbps;
        // Optional temporary cap active only inside [SpeedLimitFrom, SpeedLimitTo).
        internal long SpeedLimitKbps;
        internal string SpeedLimitFrom = "";
        internal string SpeedLimitTo = "";
        internal int SpeedLimitFromSecs = -1;
        internal int SpeedLimitToSecs = -1;
        internal string Proxy = "";
        // host -> Aria.Protect("user\npass"); never plaintext on disk.
        internal Dictionary<string, string> Credentials = new Dictionary<string, string>();
        internal string CaptureExt = "";
        internal string BlockedHosts = "";
        internal string NeatToken = "";
        internal string LastFolder = "";
        internal bool ClipboardWatch;
        internal bool ShutdownWhenDone;
        internal bool AutoSort;
        internal string NamingTemplate = "";
        internal string ScheduleAt = "";
        internal int ScheduleSecs = -1;
        internal string StopAt = "";
        internal int StopSecs = -1;
        internal bool ScheduleDaily;
        internal string Language = "en";
        internal string Prefs = "";
        internal bool UpdateCheck = true;
        internal int UpdateDays = 7;
        internal long LastUpdateCheck;
        internal int WindowWidth;
        internal int WindowHeight;
        internal int WindowX;
        internal int WindowY;
        internal bool WindowMaximized;
    }

    // Reads/writes config.txt. Values are clamped and the theme stays light
    // (the UI only ships one theme); old files without a key keep defaults.
    internal static class SettingsStore
    {
        internal static AppSettings Load(string path)
        {
            AppSettings s = new AppSettings();
            string[] lines = null;
            try { if (File.Exists(path)) lines = File.ReadAllLines(path); }
            catch { }
            if (lines == null)
            {
                // A missing or half-written config falls back to the last good copy.
                try
                {
                    string bak = path + ".bak";
                    if (File.Exists(bak)) lines = File.ReadAllLines(bak);
                }
                catch { }
            }
            try
            {
                if (lines != null)
                {
                    foreach (string l in lines)
                    {
                        if (l.StartsWith("configVersion="))
                        {
                            int v;
                            if (int.TryParse(l.Substring(14).Trim(), out v)) s.ConfigVersion = v;
                        }
                        else if (l.StartsWith("theme="))
                            s.Theme = l.Substring(6).Trim();
                        else if (l.StartsWith("maxConcurrent="))
                        {
                            int v;
                            if (int.TryParse(l.Substring(14).Trim(), out v) && v >= 1 && v <= 8) s.MaxConcurrent = v;
                        }
                        else if (l.StartsWith("partsPerDownload="))
                        {
                            int v;
                            if (int.TryParse(l.Substring(17).Trim(), out v) && v >= 1 && v <= 16)
                                s.PartsPerDownload = v;
                        }
                        else if (l.StartsWith("maxSpeedKbps="))
                        {
                            long v;
                            if (long.TryParse(l.Substring(13).Trim(), out v) && v >= 0) s.MaxSpeedKbps = v;
                        }
                        else if (l.StartsWith("speedLimitKbps="))
                        {
                            long v;
                            if (long.TryParse(l.Substring(15).Trim(), out v) && v >= 0) s.SpeedLimitKbps = v;
                        }
                        else if (l.StartsWith("speedLimitFrom="))
                            s.SpeedLimitFrom = l.Substring(15).Trim();
                        else if (l.StartsWith("speedLimitTo="))
                            s.SpeedLimitTo = l.Substring(13).Trim();
                        else if (l.StartsWith("proxy="))
                            s.Proxy = Aria.Unprotect(l.Substring(6).Trim());
                        else if (l.StartsWith("credentials="))
                            LoadCredentials(s, l.Substring(12).Trim());
                        else if (l.StartsWith("captureExt="))
                            s.CaptureExt = l.Substring(11).Trim();
                        else if (l.StartsWith("blockedHosts="))
                            s.BlockedHosts = Util.NormalizeHosts(l.Substring(13));
                        else if (l.StartsWith("neatToken="))
                            s.NeatToken = l.Substring(10).Trim();
                        else if (l.StartsWith("lastFolder="))
                            s.LastFolder = l.Substring(11).Trim();
                        else if (l.StartsWith("clipboardWatch="))
                            s.ClipboardWatch = l.Substring(15).Trim() == "1";
                        else if (l.StartsWith("shutdownWhenDone="))
                            s.ShutdownWhenDone = l.Substring(17).Trim() == "1";
                        else if (l.StartsWith("autoSort="))
                            s.AutoSort = l.Substring(9).Trim() == "1";
                        else if (l.StartsWith("namingTemplate="))
                            s.NamingTemplate = l.Substring(15).Trim();
                        else if (l.StartsWith("scheduleAt="))
                            s.ScheduleAt = l.Substring(11).Trim();
                        else if (l.StartsWith("stopAt="))
                            s.StopAt = l.Substring(7).Trim();
                        else if (l.StartsWith("scheduleDaily="))
                            s.ScheduleDaily = l.Substring(14).Trim() == "1";
                        else if (l.StartsWith("language="))
                            s.Language = l.Substring(9).Trim();
                        else if (l.StartsWith("updateCheck="))
                            s.UpdateCheck = l.Substring(12).Trim() == "1";
                        else if (l.StartsWith("updateDays="))
                        {
                            int v;
                            if (int.TryParse(l.Substring(11).Trim(), out v) && v >= 1 && v <= 365)
                                s.UpdateDays = v;
                        }
                        else if (l.StartsWith("lastUpdateCheck="))
                        {
                            long v;
                            if (long.TryParse(l.Substring(16).Trim(), out v) && v > 0) s.LastUpdateCheck = v;
                        }
                        else if (l.StartsWith("prefs="))
                            s.Prefs = l.Substring(6).Trim();
                        else if (l.StartsWith("window="))
                        {
                            string[] wp = l.Substring(7).Split(',');
                            if (wp.Length >= 5)
                            {
                                int.TryParse(wp[0], out s.WindowWidth);
                                int.TryParse(wp[1], out s.WindowHeight);
                                int.TryParse(wp[2], out s.WindowX);
                                int.TryParse(wp[3], out s.WindowY);
                                s.WindowMaximized = wp[4].Trim() == "1";
                            }
                        }
                    }
                }
            }
            catch { }

            s.Theme = "light";
            if (s.UpdateDays < 1) s.UpdateDays = 7;
            if (s.UpdateDays > 365) s.UpdateDays = 365;
            if (s.PartsPerDownload < 1) s.PartsPerDownload = 1;
            if (s.MaxSpeedKbps < 0) s.MaxSpeedKbps = 0;
            if (s.SpeedLimitKbps < 0) s.SpeedLimitKbps = 0;
            s.SpeedLimitFromSecs = Aria.ParseHhmm(s.SpeedLimitFrom);
            if (s.SpeedLimitFromSecs < 0) s.SpeedLimitFrom = "";
            s.SpeedLimitToSecs = Aria.ParseHhmm(s.SpeedLimitTo);
            if (s.SpeedLimitToSecs < 0) s.SpeedLimitTo = "";
            s.ScheduleSecs = Aria.ParseHhmm(s.ScheduleAt);
            if (s.ScheduleSecs < 0) s.ScheduleAt = "";
            s.StopSecs = Aria.ParseHhmm(s.StopAt);
            if (s.StopSecs < 0) s.StopAt = "";
            return s;
        }

        // "user\npass" for a saved host, or null when none is stored.
        internal static string SavedCredential(AppSettings s, string url)
        {
            if (s == null) return null;
            string host = Util.HostOf(url);
            string enc;
            if (host.Length == 0 || !s.Credentials.TryGetValue(host, out enc)) return null;
            string plain = Aria.Unprotect(enc);
            return plain.Length == 0 ? null : plain;
        }

        internal static void RememberCredential(AppSettings s, string url, string user, string pass)
        {
            if (s == null || Util.IsBlank(user)) return;
            string host = Util.HostOf(url);
            if (host.Length == 0) return;
            string enc = Aria.Protect(user.Trim() + "\n" + (pass == null ? "" : pass));
            if (enc.Length == 0) return;
            s.Credentials[host] = enc;
        }

        internal static void ForgetCredential(AppSettings s, string url)
        {
            if (s == null) return;
            string host = Util.HostOf(url);
            if (host.Length > 0) s.Credentials.Remove(host);
        }

        internal static void ForgetAllCredentials(AppSettings s)
        {
            if (s != null) s.Credentials.Clear();
        }

        private static string SerializeCredentials(Dictionary<string, string> creds)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in creds)
            {
                if (kv.Key.Length == 0 || kv.Value.Length == 0) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('|').Append(kv.Value);
            }
            return sb.ToString();
        }

        private static void LoadCredentials(AppSettings s, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string[] parts = value.Split(';');
            foreach (string p in parts)
            {
                int bar = p.IndexOf('|');
                if (bar <= 0 || bar >= p.Length - 1) continue;
                string host;
                try { host = Uri.UnescapeDataString(p.Substring(0, bar)).ToLowerInvariant(); }
                catch { continue; }
                string enc = p.Substring(bar + 1);
                if (host.Length == 0 || enc.Length == 0) continue;
                s.Credentials[host] = enc;
            }
        }

        internal static void Save(string path, AppSettings s)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("configVersion=1");
                sb.AppendLine("theme=" + s.Theme);
                sb.AppendLine("maxConcurrent=" + s.MaxConcurrent);
                sb.AppendLine("partsPerDownload=" + s.PartsPerDownload);
                sb.AppendLine("maxSpeedKbps=" + s.MaxSpeedKbps);
                sb.AppendLine("speedLimitKbps=" + s.SpeedLimitKbps);
                sb.AppendLine("speedLimitFrom=" + s.SpeedLimitFrom);
                sb.AppendLine("speedLimitTo=" + s.SpeedLimitTo);
                sb.AppendLine("proxy=" + Aria.Protect(s.Proxy));
                if (s.Credentials.Count > 0)
                    sb.AppendLine("credentials=" + SerializeCredentials(s.Credentials));
                sb.AppendLine("captureExt=" + s.CaptureExt);
                sb.AppendLine("blockedHosts=" + s.BlockedHosts);
                sb.AppendLine("neatToken=" + s.NeatToken);
                if (!string.IsNullOrEmpty(s.LastFolder)) sb.AppendLine("lastFolder=" + s.LastFolder);
                sb.AppendLine("clipboardWatch=" + (s.ClipboardWatch ? 1 : 0));
                sb.AppendLine("shutdownWhenDone=" + (s.ShutdownWhenDone ? 1 : 0));
                sb.AppendLine("autoSort=" + (s.AutoSort ? 1 : 0));
                if (!string.IsNullOrEmpty(s.NamingTemplate)) sb.AppendLine("namingTemplate=" + s.NamingTemplate);
                sb.AppendLine("scheduleAt=" + s.ScheduleAt);
                sb.AppendLine("stopAt=" + s.StopAt);
                sb.AppendLine("scheduleDaily=" + (s.ScheduleDaily ? 1 : 0));
                sb.AppendLine("language=" + s.Language);
                sb.AppendLine("updateCheck=" + (s.UpdateCheck ? 1 : 0));
                sb.AppendLine("updateDays=" + s.UpdateDays);
                if (s.LastUpdateCheck > 0) sb.AppendLine("lastUpdateCheck=" + s.LastUpdateCheck);
                sb.AppendLine("prefs=" + s.Prefs);
                if (s.WindowWidth > 0 && s.WindowHeight > 0)
                    sb.AppendLine("window=" + s.WindowWidth + "," + s.WindowHeight + "," +
                                  s.WindowX + "," + s.WindowY + "," +
                                  (s.WindowMaximized ? 1 : 0));
                // Write beside the target, then swap. A crash mid-write leaves the
                // old config (or .bak) readable instead of a truncated file.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, path + ".bak"); }
                    catch
                    {
                        try { File.Delete(path + ".bak"); } catch { }
                        try { File.Move(path, path + ".bak"); } catch { }
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch { }
        }
    }
}
