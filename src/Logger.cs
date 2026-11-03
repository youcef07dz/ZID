using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZID
{
    // One writer for app.log: bounded size (one rotated backup), and every line
    // passes through Mask so cookies, tokens and URL credentials stay out.
    internal static class Logger
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object _lock = new object();
        private static string _path = "";

        internal static void Init(string path) { _path = path; }
        internal static string Path { get { return _path; } }

        internal static void Debug(string msg) { Write("DEBUG", msg); }
        internal static void Info(string msg)  { Write("INFO ", msg); }
        internal static void Warn(string msg)  { Write("WARN ", msg); }
        internal static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            if (string.IsNullOrEmpty(_path)) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + level + "  " + Mask(msg);
            try
            {
                lock (_lock)
                {
                    string dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    Rotate();
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch { }
        }

        // Keep one old file; rotating beats deleting the only diagnostics.
        private static void Rotate()
        {
            FileInfo fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            try { File.Delete(_path + ".1"); } catch { }
            File.Move(_path, _path + ".1");
        }

        internal static string Mask(string msg)
        {
            if (msg == null) return "";
            msg = Regex.Replace(msg,
                @"(?im)^\s*(cookie|authorization|proxy-authorization)\s*[:=].*$",
                "$1: <masked>");
            msg = Regex.Replace(msg,
                @"(?i)\b(token|secret|passwd|password|pwd)=([^&\s;]+)",
                "$1=<masked>");
            msg = Regex.Replace(msg,
                @"(?i)\b([a-z][a-z0-9+.-]*://)([^/\s:@]+):([^/\s@]+)@",
                "$1$2:<masked>@");
            return msg;
        }

        // Tail of the log for the copy-diagnostics action.
        internal static string Tail(int maxLines)
        {
            try
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return "";
                    string[] all = File.ReadAllLines(_path);
                    int start = all.Length - maxLines;
                    if (start < 0) start = 0;
                    StringBuilder sb = new StringBuilder();
                    for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return "log unreadable: " + ex.Message; }
        }

        // Last ERROR lines only, newest last; About shows this so users do not
        // have to find app.log. Continuation lines of a stack trace are kept out.
        internal static string RecentErrors(int maxLines)
        {
            try
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return "";
                    string[] all = File.ReadAllLines(_path);
                    List<string> found = new List<string>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i].IndexOf("  ERROR  ", StringComparison.Ordinal) < 0) continue;
                        found.Add(all[i]);
                        if (found.Count > maxLines) found.RemoveAt(0);
                    }
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < found.Count; i++) sb.AppendLine(found[i]);
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex) { return "log unreadable: " + ex.Message; }
        }
    }
}
