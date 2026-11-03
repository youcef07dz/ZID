using System;
using System.Collections.Generic;

namespace ZID
{
    // Engine-neutral task status. aria2's strings are mapped here so the rest
    // of the app does not compare engine-specific text.
    internal enum EngineTaskStatus
    {
        Unknown,
        Active,
        Waiting,
        Paused,
        Complete,
        Error,
        Removed
    }

    // How a failure should be handled (Section 12 of the plan).
    internal enum EngineErrorClass
    {
        Unknown,
        Transient,
        Network,
        Permanent,
        Local,
        Integrity
    }

    // One place that turns an engine error code into text the UI can show.
    internal static class EngineError
    {
        public static EngineErrorClass Classify(int code, string msg)
        {            if (msg != null)
            {
                if (msg.IndexOf("429", StringComparison.Ordinal) >= 0 ||
                    msg.IndexOf("Retry-After", StringComparison.OrdinalIgnoreCase) >= 0)
                    return EngineErrorClass.Transient;
                if (msg.IndexOf("404", StringComparison.Ordinal) >= 0 ||
                    msg.IndexOf("410", StringComparison.Ordinal) >= 0)
                    return EngineErrorClass.Permanent;
            }
            switch (code)
            {
                case 3:  case 4:  case 8:  case 11:
                case 23: case 24: return EngineErrorClass.Permanent;
                case 9:  case 13: case 15: case 16: case 17: case 18:
                    return EngineErrorClass.Local;
                case 19: return EngineErrorClass.Network;
                case 32: return EngineErrorClass.Integrity;
                case 1:  case 2:  case 5:  case 6:  case 22: case 29:
                    return EngineErrorClass.Transient;
                default: return EngineErrorClass.Unknown;
            }
        }

        // aria2 code 24 is HTTP authorization failure; a 407 from the proxy
        // arrives as the same code with the status in the message.
        public static bool IsAuth(int code, string msg)
        {
            if (code == 24) return true;
            if (msg == null) return false;
            return msg.IndexOf(" 401", StringComparison.Ordinal) >= 0 ||
                   msg.IndexOf(" 407", StringComparison.Ordinal) >= 0 ||
                   msg.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("Proxy Authentication", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string Text(int code, string msg)
        {
            // aria2's own checksum text varies ("Checksum error detected." etc).
            if (code == 32) return "Checksum mismatch";
            if (msg != null && msg.Trim().Length > 0) return msg.Trim();
            switch (code)
            {
                case 1:  return "download failed";
                case 2:  return "operation timed out";
                case 3:  return "resource not found";
                case 4:  return "resource not found (retry limit)";
                case 5:  return "download speed too slow";
                case 6:  return "network problem";
                case 7:  return "unfinished downloads remain";
                case 8:  return "server cannot resume";
                case 9:  return "not enough disk space";
                case 11: return "the same file is already downloading";
                case 13: return "file already exists";
                case 15: return "cannot open the existing file";
                case 16: return "cannot create the file";
                case 17: return "file I/O error";
                case 18: return "cannot create the folder";
                case 19: return "DNS lookup failed";
                case 21: return "FTP error";
                case 22: return "bad HTTP response (check the URL)";
                case 23: return "too many redirects";
                case 24: return "authorization failed (cookies?)";
                case 29: return "server busy, try again later";
                case 32: return "checksum mismatch";
                default: return "aria2 exit code " + code;
            }
        }

        // Turns an exception message into something a user can act on. The
        // technical text stays in app.log/Diagnostics; unknown cases stay short.
        public static string UserText(string msg)
        {
            if (msg == null) return "Download failed";
            string m = msg.ToLowerInvariant();
            if (m.Trim().Length == 0) return "Download failed";
            if (m.IndexOf("not enough disk space") >= 0 || m.IndexOf("disk is full") >= 0)
                return "Not enough disk space";
            if (m.IndexOf("aria2c.exe not found") >= 0) return "Download engine not found";
            if (m.IndexOf("actively refused") >= 0 || m.IndexOf("connection refused") >= 0)
                return "Cannot connect to the download engine";
            if (m.IndexOf("timed out") >= 0 || m.IndexOf("timeout") >= 0)
                return "Connection timed out";
            if (m.IndexOf("name resolution") >= 0 || m.IndexOf("no such host") >= 0 ||
                m.IndexOf("host not found") >= 0 || m.IndexOf("dns") >= 0)
                return "Server address could not be resolved";
            if (m.IndexOf("network path") >= 0 || m.IndexOf("network is unreachable") >= 0)
                return "Network unavailable";
            if (m.IndexOf("access is denied") >= 0 || m.IndexOf("unauthorized") >= 0)
                return "Access denied";
            if (m.IndexOf("too many redirects") >= 0) return "Too many redirects";
            return "Download failed";
        }
    }

    internal sealed class EngineTaskState
    {
        public string Gid = "";
        public string LocalPath = "";
        public string Url = "";
        public EngineTaskStatus Status;
        public long TotalLength;
        public long CompletedLength;
        public long DownloadSpeed;
        public int  Connections;
        public int  ErrorCode;
        public string ErrorMessage = "";
        public string ErrorText = "";

        internal static EngineTaskStatus Parse(string status)
        {
            switch (status)
            {
                case "active":   return EngineTaskStatus.Active;
                case "waiting":  return EngineTaskStatus.Waiting;
                case "paused":   return EngineTaskStatus.Paused;
                case "complete": return EngineTaskStatus.Complete;
                case "error":    return EngineTaskStatus.Error;
                case "removed":  return EngineTaskStatus.Removed;
                default:         return EngineTaskStatus.Unknown;
            }
        }
    }

    // The form talks to this instead of a specific downloader, so a fake or a
    // second engine can replace aria2 without touching download lifecycle code.
    internal interface IDownloadEngine
    {
        bool EnsureStarted();
        // After a failed call: true when the engine is usable again.
        bool Recover();
        string LastError { get; }
        void Stop();
        string Add(DownloadItem item, string defaultFolder, int defaultParts,
                   long defaultSpeed, string defaultProxy);
        List<string> ListGids();
        EngineTaskState Tell(string gid);
        void Pause(string gid);
        void Unpause(string gid);
        void Remove(string gid);
        void RemoveResult(string gid);
        void ChangeOption(string gid, string name, string value);
    }

    internal sealed class Aria2Engine : IDownloadEngine
    {
        private readonly AriaRpc _rpc;
        private readonly object _recoverLock = new object();
        private readonly RestartGuard _restartGuard = new RestartGuard();

        internal Aria2Engine(string exe, string toolsDir, string certs)
        {
            _rpc = new AriaRpc(exe, toolsDir, certs);
        }

        public bool EnsureStarted() { return _rpc.EnsureStarted(); }
        public string LastError { get { return _rpc.LastError; } }
        public void Stop() { _rpc.Stop(); }

        // A dead daemon is restarted at most 3 times in 60 s; after that the
        // caller surfaces "engine unavailable" instead of spawn-looping.
        public bool Recover()
        {
            if (_rpc.Ping()) return true;
            lock (_recoverLock)
            {
                if (!_restartGuard.Allow(DateTime.UtcNow)) return false;
            }
            return _rpc.EnsureStarted();
        }

        public string Add(DownloadItem item, string defaultFolder, int defaultParts,
                          long defaultSpeed, string defaultProxy)
        {
            return _rpc.Add(item, defaultFolder, defaultParts, defaultSpeed, defaultProxy);
        }

        public List<string> ListGids() { return _rpc.ListGids(); }

        public EngineTaskState Tell(string gid)
        {
            System.Collections.Generic.Dictionary<string, object> d = _rpc.Tell(gid);
            EngineTaskState s = new EngineTaskState();
            s.Gid             = AriaRpc.Str(d, "gid");
            s.LocalPath       = AriaRpc.FirstFilePath(d);
            s.Url             = AriaRpc.FirstUri(d);
            s.Status          = EngineTaskState.Parse(AriaRpc.Str(d, "status"));
            s.TotalLength     = AriaRpc.Long(d, "totalLength");
            s.CompletedLength = AriaRpc.Long(d, "completedLength");
            s.DownloadSpeed   = AriaRpc.Long(d, "downloadSpeed");
            s.Connections     = (int)AriaRpc.Long(d, "connections");
            s.ErrorCode       = (int)AriaRpc.Long(d, "errorCode");
            s.ErrorMessage    = AriaRpc.Str(d, "errorMessage");
            s.ErrorText       = EngineError.Text(s.ErrorCode, s.ErrorMessage);
            return s;
        }

        public void Pause(string gid) { _rpc.Pause(gid); }
        public void Unpause(string gid) { _rpc.Unpause(gid); }
        public void Remove(string gid) { _rpc.Remove(gid); }
        public void RemoveResult(string gid) { _rpc.RemoveResult(gid); }
        public void ChangeOption(string gid, string name, string value)
        {
            _rpc.ChangeOption(gid, name, value);
        }
    }
}
