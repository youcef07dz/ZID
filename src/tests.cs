using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ZID
{
    internal static class Tests
    {
        private static int _fail;

        private static void Check(bool ok, string name)
        {
            if (ok) { Console.WriteLine("ok   " + name); return; }
            _fail++;
            Console.WriteLine("FAIL " + name);
        }

        // Expandable stream that already holds the request bytes.
        private static MemoryStream Req(string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            MemoryStream m = new MemoryStream(b.Length + 2048);
            m.Write(b, 0, b.Length);
            m.Position = 0;
            return m;
        }

        private static byte[] ReadAll(Stream s)
        {
            MemoryStream m = new MemoryStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) m.Write(buf, 0, n);
            return m.ToArray();
        }

        private static bool BytesEqual(byte[] a, int ao, byte[] b, int bo, int count)
        {
            for (int i = 0; i < count; i++)
                if (a[ao + i] != b[bo + i]) return false;
            return true;
        }

        private static int RangeStart(string range)
        {
            if (string.IsNullOrEmpty(range) || range == "full") return -1;
            int dash = range.IndexOf('-');
            int start;
            return dash > 0 && int.TryParse(range.Substring(0, dash), out start) ? start : -1;
        }

        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--bench", StringComparison.OrdinalIgnoreCase))
            {
                Benchmark.Run();
                return;
            }

            Check(Theme.Mix(Color.Black, Color.White, 0.5) == Color.FromArgb(128, 128, 128), "theme mix midpoint");
            Theme.SetAccent(Color.FromArgb(5, 150, 105));
            Check(Theme.Accent == Color.FromArgb(5, 150, 105) &&
                  Theme.AccentBorder != Theme.AccentHover, "theme accent follows the page choice");
            Theme.SetAccent(Color.FromArgb(99, 102, 241));
            Check(EngineError.Text(19, null) == "DNS lookup failed", "error 19");
            Check(EngineError.Text(22, "").IndexOf("HTTP") >= 0, "error 22 http hint");
            Check(EngineError.Text(999, null) == "aria2 exit code 999", "error unknown code");
            Check(EngineError.Text(2, "custom text") == "custom text", "error prefers engine text");
            Check(EngineError.Text(32, "Checksum error detected.") == "Checksum mismatch", "error 32 checksum");
            Check(EngineError.UserText(null) == "Download failed" &&
                  EngineError.UserText("") == "Download failed" &&
                  EngineError.UserText("System.NullReferenceException") == "Download failed",
                  "user error fallback");
            Check(EngineError.UserText("aria2c.exe not found in C:\\x") == "Download engine not found",
                  "user error engine missing");
            Check(EngineError.UserText("No connection could be made because the target machine actively refused it")
                  == "Cannot connect to the download engine", "user error engine refused");
            Check(EngineError.UserText("The operation has timed out") == "Connection timed out",
                  "user error timeout");
            Check(EngineError.UserText("No such host is known") == "Server address could not be resolved",
                  "user error dns");
            Check(EngineError.UserText("There is not enough disk space") == "Not enough disk space",
                  "user error disk full");
            Check(EngineError.UserText("Access is denied") == "Access denied", "user error access");
            Check(EngineError.UserText("Too many redirects") == "Too many redirects",
                  "user error redirects");

            Check(EngineError.Classify(3, null) == EngineErrorClass.Permanent &&
                  EngineError.Classify(24, null) == EngineErrorClass.Permanent,
                  "engine classify permanent");
            Check(EngineError.Classify(19, null) == EngineErrorClass.Network,
                  "engine classify network");
            Check(EngineError.Classify(9, null) == EngineErrorClass.Local &&
                  EngineError.Classify(32, null) == EngineErrorClass.Integrity,
                  "engine classify local/integrity");
            Check(EngineError.Classify(22, "HTTP 429") == EngineErrorClass.Transient &&
                  EngineError.Classify(22, "HTTP 404") == EngineErrorClass.Permanent,
                  "engine classify by message");
            Check(EngineError.IsAuth(24, null) &&
                  EngineError.IsAuth(22, "HTTP 401 Unauthorized") &&
                  EngineError.IsAuth(22, "HTTP 407 Proxy Authentication Required") &&
                  !EngineError.IsAuth(22, "HTTP 404"), "engine auth detection");
            Check(RetryPolicy.IsRetryable(EngineErrorClass.Transient) &&
                  RetryPolicy.IsRetryable(EngineErrorClass.Network) &&
                  !RetryPolicy.IsRetryable(EngineErrorClass.Permanent) &&
                  !RetryPolicy.IsRetryable(EngineErrorClass.Integrity),
                  "retry policy classes");
            Check(RetryPolicy.DelayMs(1, null) == 2000 &&
                  RetryPolicy.DelayMs(3, null) == 8000 &&
                  RetryPolicy.DelayMs(9, null) == 60000, "retry delay backoff");
            Check(RetryPolicy.DelayMs(1, "Retry-After: 12") == 12000,
                  "retry after wins over backoff");
            Check(RetryPolicy.RetryAfterMs("Retry-After: 0") == 1000 &&
                  RetryPolicy.RetryAfterMs("no header") == 0 &&
                  RetryPolicy.RetryAfterMs(null) == 0, "retry after bounds");

            Check(RetryPolicy.IsNetworkError("Error: Network unavailable") &&
                  RetryPolicy.IsNetworkError("Error: Connection timed out") &&
                  RetryPolicy.IsNetworkError("Error: DNS lookup failed") &&
                  !RetryPolicy.IsNetworkError("Error: Access denied") &&
                  !RetryPolicy.IsNetworkError(null), "network error text");
            Check(RetryPolicy.Decide(EngineErrorClass.Network, 0, false) == RetryDecision.WaitNetwork &&
                  RetryPolicy.Decide(EngineErrorClass.Unknown, 0, false) == RetryDecision.WaitNetwork &&
                  RetryPolicy.Decide(EngineErrorClass.Network, 0, true) == RetryDecision.Retry &&
                  RetryPolicy.Decide(EngineErrorClass.Transient, 4, true) == RetryDecision.Retry &&
                  RetryPolicy.Decide(EngineErrorClass.Transient, 5, true) == RetryDecision.Fail &&
                  RetryPolicy.Decide(EngineErrorClass.Permanent, 0, true) == RetryDecision.Fail &&
                  RetryPolicy.Decide(EngineErrorClass.Integrity, 0, true) == RetryDecision.Fail &&
                  RetryPolicy.Decide(EngineErrorClass.Local, 0, false) == RetryDecision.Fail,
                  "retry decision");

            DownloadItem pr = new DownloadItem();
            pr.Size = 1000;
            pr.Eta = "old";
            ProgressPolicy.Update(pr, 2000, 500, 100, 4);
            Check(pr.Size == 2000 && pr.BytesReceived == 500 && pr.Speed == 100 &&
                  pr.ActiveConnections == 4 && pr.Progress == 25 && pr.Eta == "00:00:15",
                  "progress updates size, percent and eta");

            DownloadItem prZero = new DownloadItem();
            prZero.Size = 0;
            prZero.Eta = "old";
            prZero.Progress = 7;
            ProgressPolicy.Update(prZero, 0, 10, 0, 0);
            Check(prZero.Size == 0 && prZero.BytesReceived == 10 && prZero.Progress == 7 &&
                  prZero.Eta == "old" && prZero.Speed == 0 && prZero.ActiveConnections == 0,
                  "progress keeps unknown size and eta");

            DownloadItem prDone = new DownloadItem();
            prDone.Size = 100;
            prDone.Eta = "old";
            ProgressPolicy.Update(prDone, 0, 120, 50, 2);
            Check(prDone.Size == 100 && prDone.Progress == 120 && prDone.Eta == "old" &&
                  prDone.BytesReceived == 120, "progress stops eta past 100");

            DownloadManager workerManager = new DownloadManager();
            DownloadItem ws = new DownloadItem();
            ws.State = DownloadState.Downloading;
            ws.Speed = 5;
            ws.ActiveConnections = 2;
            ws.Eta = "x";
            Check(!WorkerState.Apply(ws, workerManager, EngineTaskStatus.Paused) &&
                  ws.State == DownloadState.Paused && ws.Eta == "Paused" &&
                  ws.Speed == 0 && ws.ActiveConnections == 0, "worker status pauses");

            ws.State = DownloadState.Downloading;
            ws.Eta = "x";
            Check(!WorkerState.Apply(ws, workerManager, EngineTaskStatus.Waiting) &&
                  ws.State == DownloadState.Queued && ws.Eta == "Queued",
                  "worker status queues");

            ws.State = DownloadState.Downloading;
            ws.EngineId = "g";
            ws.Eta = "x";
            Check(WorkerState.Apply(ws, workerManager, EngineTaskStatus.Removed) &&
                  ws.State == DownloadState.Paused && ws.EngineId == null && ws.Eta == "Paused",
                  "worker status removes");

            ws.State = DownloadState.Downloading;
            ws.EngineId = "g";
            ws.Eta = "x";
            Check(!WorkerState.Apply(ws, workerManager, EngineTaskStatus.Active) &&
                  ws.State == DownloadState.Downloading && ws.EngineId == "g" && ws.Eta == "x",
                  "worker status ignores active");

            FakeWorkerHost downHost = new FakeWorkerHost();
            downHost.EngineResult = false;
            downHost.EngineErrorText = "down";
            DownloadItem downItem = new DownloadItem();
            downItem.Generation = 3;
            downItem.State = DownloadState.Starting;
            new DownloadWorker(downHost).Run(downItem, 3);
            Check(downItem.State == DownloadState.Failed &&
                  downItem.Eta == "Error: download engine unavailable" &&
                  !downItem.ResumeOnLoad && downHost.PushCount == 1 &&
                  downHost.FlushCount == 1 && downHost.StartNextCount == 1,
                  "worker fails when the engine is unavailable");

            string workerExe = Path.Combine(Path.GetTempPath(),
                "zid_worker_" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(workerExe, "x");
            FakeWorkerHost spaceHost = new FakeWorkerHost();
            spaceHost.AriaExePath = workerExe;
            spaceHost.FreeBytes = 0;
            DownloadItem spaceItem = new DownloadItem();
            spaceItem.Generation = 1;
            spaceItem.State = DownloadState.Starting;
            spaceItem.Size = 100;
            new DownloadWorker(spaceHost).Run(spaceItem, 1);
            Check(spaceItem.State == DownloadState.Failed &&
                  spaceItem.Eta == "Error: not enough disk space" &&
                  spaceHost.Fake.Calls.Count == 0 && spaceHost.PushCount == 1 &&
                  spaceHost.FlushCount == 1, "worker fails on low disk space");

            FakeWorkerHost authHost = new FakeWorkerHost();
            authHost.AriaExePath = workerExe;
            authHost.AskResult = true;
            EngineTaskState authError = new EngineTaskState();
            authError.Status = EngineTaskStatus.Error;
            authError.ErrorCode = 24;
            EngineTaskState authRemoved = new EngineTaskState();
            authRemoved.Status = EngineTaskStatus.Removed;
            authHost.Fake.Script.Add(authError);
            authHost.Fake.Script.Add(authRemoved);
            DownloadItem authItem = new DownloadItem();
            authItem.Generation = 1;
            authItem.State = DownloadState.Starting;
            new DownloadWorker(authHost).Run(authItem, 1);
            Check(authHost.AskCount == 1 && authItem.AuthUser == "u" &&
                  authItem.AuthPass == "p" && authItem.State == DownloadState.Paused,
                  "worker asks for credentials once and retries");
            try { File.Delete(workerExe); } catch { }

            FakeWorkerHost doneHost = new FakeWorkerHost();
            DownloadItem doneItem = new DownloadItem();
            doneItem.Generation = 2;
            doneItem.State = DownloadState.Completed;
            new DownloadWorker(doneHost).Run(doneItem, 2);
            Check(doneItem.State == DownloadState.Completed &&
                  doneHost.Fake.Calls.Count == 0 && doneHost.PushCount == 1 &&
                  doneHost.StartNextCount == 1,
                  "worker leaves a settled row alone");

            DownloadItem nwWait = new DownloadItem();
            nwWait.Url = "http://example.com/a.bin";
            nwWait.State = DownloadState.WaitingNetwork;
            DownloadItem nwFailed = new DownloadItem();
            nwFailed.Url = "http://example.com/b.bin";
            nwFailed.State = DownloadState.Failed;
            nwFailed.Eta = "Error: network unreachable";
            DownloadItem nwMerged = new DownloadItem();
            nwMerged.Url = "http://example.com/c.bin";
            nwMerged.State = DownloadState.Failed;
            nwMerged.Eta = "Error: network unreachable";
            nwMerged.Merged = true;
            DownloadItem nwOther = new DownloadItem();
            nwOther.Url = "http://example.com/d.bin";
            nwOther.State = DownloadState.Failed;
            nwOther.Eta = "Error: 404";
            List<DownloadItem> nwSnap = new List<DownloadItem>();
            nwSnap.Add(nwWait); nwSnap.Add(nwFailed); nwSnap.Add(nwMerged); nwSnap.Add(nwOther);

            bool nwWaiting, nwFailedFlag;
            string nwProbe;
            NetworkRecovery.Scan(nwSnap, false, out nwWaiting, out nwFailedFlag, out nwProbe);
            Check(nwWaiting && !nwFailedFlag && nwProbe == nwWait.Url,
                  "network scan finds waiting row and probe url");
            NetworkRecovery.Scan(nwSnap, true, out nwWaiting, out nwFailedFlag, out nwProbe);
            Check(nwWaiting && nwFailedFlag && nwProbe == nwWait.Url,
                  "network scan adds legacy failures when restored");
            List<DownloadItem> nwFailOnly = new List<DownloadItem>();
            nwFailOnly.Add(nwMerged); nwFailOnly.Add(nwOther);
            NetworkRecovery.Scan(nwFailOnly, true, out nwWaiting, out nwFailedFlag, out nwProbe);
            Check(!nwWaiting && !nwFailedFlag && nwProbe == null,
                  "network scan skips merged and non-network failures");

            Check(NetworkRecovery.Throttled(1000, 5000, false) &&
                  !NetworkRecovery.Throttled(1000, 11000, false) &&
                  !NetworkRecovery.Throttled(1000, 1000, true),
                  "network scan throttles the ticker only");

            FakeNetworkHost netHost = new FakeNetworkHost();
            new NetworkRecovery(netHost).Resume(nwSnap, true);
            Check(netHost.Resumed.Count == 2 && netHost.Resumed[0] == nwWait &&
                  netHost.Resumed[1] == nwFailed && netHost.PushCount == 1 &&
                  netHost.LogCount == 1, "network resume wakes waiting and legacy rows");

            FakeNetworkHost probeHost = new FakeNetworkHost();
            probeHost.Available = false;
            new NetworkRecovery(probeHost).ProbeNow(nwSnap, true, nwWait.Url);
            Check(probeHost.Resumed.Count == 0 && probeHost.PushCount == 0,
                  "network probe stops when the adapter is down");
            probeHost.Available = true;
            probeHost.ProbeResult = false;
            new NetworkRecovery(probeHost).ProbeNow(nwSnap, true, nwWait.Url);
            Check(probeHost.ProbeCount == 1 && probeHost.Resumed.Count == 0,
                  "network probe stops when the host is unreachable");
            probeHost.ProbeResult = true;
            new NetworkRecovery(probeHost).ProbeNow(nwSnap, true, nwWait.Url);
            Check(probeHost.Resumed.Count == 2 && probeHost.PushCount == 1,
                  "network probe resumes after a successful probe");

            DownloadItem startItem = new DownloadItem();
            Check(AddService.StartKind(startItem, true) == AddStart.Start &&
                  AddService.StartKind(startItem, false) == AddStart.Paused,
                  "add start honors the start flag");
            startItem.Auto = true;
            startItem.Kind = "audio";
            Check(AddService.StartKind(startItem, true) == AddStart.WaitForVideo,
                  "add start waits for hidden audio");
            startItem.Auto = false;
            Check(AddService.StartKind(startItem, true) == AddStart.Start,
                  "add start runs a manual row");

            CapturePolicy clip = new CapturePolicy();
            Check(clip.Accept("https://example.com/a.zip", 1000) &&
                  !clip.Accept("https://example.com/a.zip", 2500) &&
                  clip.Accept("https://example.com/a.zip", 3500),
                  "capture debounces the same url");
            Check(new CapturePolicy().Accept("ftp://example.com/f.bin", 0) &&
                  !new CapturePolicy().Accept("not a url", 0) &&
                  !new CapturePolicy().Accept("https://e.com/\nsecond", 0) &&
                  !new CapturePolicy().Accept("https://e.com/" + new string('a', 2100), 0),
                  "capture only takes one supported url");
            CapturePolicy self = new CapturePolicy();
            self.NoteSelfCopy("https://example.com/x.zip");
            Check(!self.Accept("https://example.com/x.zip", 0) &&
                  self.Accept("https://example.com/x.zip", 5000),
                  "capture ignores ZID's own copy once");

            RestartGuard guard = new RestartGuard();
            DateTime gt = new DateTime(2026, 1, 1, 12, 0, 0);
            Check(guard.Allow(gt) && guard.Allow(gt.AddSeconds(1)) && guard.Allow(gt.AddSeconds(2)) &&
                  !guard.Allow(gt.AddSeconds(3)), "restart guard allows three then blocks");
            Check(guard.Allow(gt.AddSeconds(61)), "restart guard window slides");

            Check(!Util.NeedsMoreSpace(0, 0, 0) && !Util.NeedsMoreSpace(0, 0, 100),
                  "disk check unknown size never blocks");
            Check(!Util.NeedsMoreSpace(1000, 400, 600) && !Util.NeedsMoreSpace(1000, 400, 5000),
                  "disk check enough space");
            Check(Util.NeedsMoreSpace(1000, 400, 599) && Util.NeedsMoreSpace(1000, 0, 0),
                  "disk check not enough space");

            string pageDir = Path.Combine(Path.GetTempPath(), "opencode\\zid_page_test");
            try { Directory.CreateDirectory(pageDir); } catch { }
            string page = Path.Combine(pageDir, "video.mp4");
            File.WriteAllText(page, "\r\n\t <!DOCTYPE html><html><body>Wi-Fi login</body></html>");
            Check(Util.LooksLikeWebPage(page, "video.mp4"), "web page check catches a portal body");
            File.WriteAllText(page, "\uFEFF<html><head></head></html>");
            Check(Util.LooksLikeWebPage(page, "video.mp4"), "web page check skips a UTF-8 BOM");
            File.WriteAllText(page, "<html><body>UTF-16 LE portal</body></html>",
                              Encoding.Unicode);
            Check(Util.LooksLikeWebPage(page, "video.mp4"),
                  "web page check catches UTF-16 LE");
            File.WriteAllText(page, "<html><body>UTF-16 BE portal</body></html>",
                              Encoding.BigEndianUnicode);
            Check(Util.LooksLikeWebPage(page, "video.mp4"),
                  "web page check catches UTF-16 BE");
            File.WriteAllText(page, "plain UTF-16 text", Encoding.Unicode);
            Check(!Util.LooksLikeWebPage(page, "video.mp4"),
                  "web page check allows UTF-16 text");
            File.WriteAllText(page, "");
            Check(!Util.LooksLikeWebPage(page, "video.mp4"), "web page check ignores an empty file");
            File.WriteAllText(page, "ID3\u0004\0\0binary-data");
            Check(!Util.LooksLikeWebPage(page, "video.mp4"), "web page check allows a real file");
            string savedPage = Path.Combine(pageDir, "page.html");
            File.WriteAllText(savedPage, "<html><body>saved page</body></html>");
            Check(!Util.LooksLikeWebPage(savedPage, "page.html"), "web page check allows a .html file");
            Check(!Util.LooksLikeWebPage(Path.Combine(pageDir, "missing.mp4"), "missing.mp4"),
                  "web page check ignores a missing file");
            try { Directory.Delete(pageDir, true); } catch { }

            Check(AppPaths.UsePortable(true, false), "portable: fresh writable folder");
            Check(!AppPaths.UsePortable(true, true), "portable: existing AppData install wins");
            Check(!AppPaths.UsePortable(false, false), "portable: read-only folder falls back");
            Check(!AppPaths.UsePortable(false, true), "portable: read-only existing install");

            string forcedData = Path.Combine(Path.GetTempPath(), "opencode\\zid_forced_data");
            try { if (Directory.Exists(forcedData)) Directory.Delete(forcedData, true); } catch { }
            string prevDataDir = Environment.GetEnvironmentVariable("ZID_DATA_DIR");
            Environment.SetEnvironmentVariable("ZID_DATA_DIR", forcedData);
            AppPaths.Init();
            Check(AppPaths.Portable && AppPaths.DataDir == Path.GetFullPath(forcedData) &&
                  Directory.Exists(forcedData), "data dir override is used");
            Environment.SetEnvironmentVariable("ZID_DATA_DIR", prevDataDir);
            try { Directory.Delete(forcedData, true); } catch { }

            string oldProfile = Path.Combine(Path.GetTempPath(), "opencode\\zid_old_profile");
            string newProfile = Path.Combine(Path.GetTempPath(), "opencode\\zid_new_profile");
            try { if (Directory.Exists(oldProfile)) Directory.Delete(oldProfile, true); } catch { }
            try { if (Directory.Exists(newProfile)) Directory.Delete(newProfile, true); } catch { }
            Directory.CreateDirectory(oldProfile);
            File.WriteAllText(Path.Combine(oldProfile, "config.txt"), "prefs=a=1");
            Check(AppPaths.MigrateProfile(oldProfile, newProfile) &&
                  File.ReadAllText(Path.Combine(newProfile, "config.txt")) == "prefs=a=1",
                  "old profile is imported once");
            try { Directory.Delete(oldProfile, true); } catch { }
            try { Directory.Delete(newProfile, true); } catch { }


            Check(Aria.JsonEscape("a\"b\\c\n\t") == "a\\\"b\\\\c\\n\\t", "json escape");
            Check(Aria.JsonEscape(null) == "", "json escape null");
            Check(Aria.JsonEscape("\u0001") == "\\u0001", "json escape control char");

            Dictionary<string, object> j = Aria.ParseJson(
                "{\"status\":\"active\",\"totalLength\":\"123\",\"ok\":true," +
                "\"files\":[{\"path\":\"a/b\"}],\"x\":null}") as Dictionary<string, object>;
            Check(j != null && AriaRpc.Str(j, "status") == "active", "json parse string");
            Check(AriaRpc.Long(j, "totalLength") == 123, "json parse number string");
            Check(AriaRpc.Long(j, "missing") == 0, "json parse missing");
            List<object> jf = j == null ? null : j["files"] as List<object>;
            Check(jf != null && jf.Count == 1 &&
                  AriaRpc.Str(jf[0] as Dictionary<string, object>, "path") == "a/b",
                  "json parse nested array");
            Check(Aria.ParseJson("\"a\\\"b\\\\c\\n\\u0041\"") as string == "a\"b\\c\nA", "json parse escapes");
            Check(Aria.ParseJson("[1,2.5,-3e2]") as List<object> != null, "json parse numbers");

            Check(Aria.ParseHhmm("01:30") == 5400, "hhmm normal");
            Check(Aria.ParseHhmm(" 23:59 ") == 86340, "hhmm padded");
            Check(Aria.ParseHhmm("") == -1 && Aria.ParseHhmm(null) == -1, "hhmm empty");
            Check(Aria.ParseHhmm("24:00") == -1 && Aria.ParseHhmm("7") == -1, "hhmm invalid");
            Check(Aria.TypeFolder("MP4") == "Video" && Aria.TypeFolder("zip") == "Compressed", "type folder");
            Check(Aria.TypeFolder("") == "Other" && Aria.TypeFolder("XYZ") == "Other", "type folder other");
            Check(Playlist.IsHls("https://x/a/master.m3u8?t=1") && !Playlist.IsHls("https://x/a/v.mp4"),
                  "hls detect");
            Check(Playlist.IsDash("https://x/a/manifest.mpd") && !Playlist.IsDash("https://x/a/v.mp4"),
                  "dash detect");

            Dictionary<string, string> en = new Dictionary<string, string>();
            en["a"] = "A"; en["b"] = "B";
            Dictionary<string, string> es = new Dictionary<string, string>();
            es["b"] = "BB";
            Dictionary<string, string> mg = Lang.Merge(en, es);
            Check(mg["a"] == "A" && mg["b"] == "BB", "lang merge fallback");
            Check(Lang.Get(mg, "missing", "fallback") == "fallback", "lang get fallback");
            Check(Lang.Status(mg, "Downloading") == "Downloading", "lang status passthrough");

            string tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "# comment\ncode=xx\nname=Test\ndir=rtl\na = Hello\n");
            Dictionary<string, string> ld = Lang.Load(tmp);
            File.Delete(tmp);
            Check(ld["code"] == "xx" && ld["name"] == "Test" && ld["dir"] == "rtl" && ld["a"] == "Hello",
                  "lang load file");

            List<BrowserInfo> browsers = new List<BrowserInfo>();
            browsers.AddRange(Browsers.Find(false));
            browsers.AddRange(Browsers.Find(true));
            Dictionary<string, bool> paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool scanOk = true;
            foreach (BrowserInfo bi in browsers)
            {
                if (!File.Exists(bi.Path) || paths.ContainsKey(bi.Path)) scanOk = false;
                paths[bi.Path] = true;
            }
            bool familiesOk = true;
            foreach (BrowserInfo bi in Browsers.Find(false)) if (bi.Firefox) familiesOk = false;
            foreach (BrowserInfo bi in Browsers.Find(true)) if (!bi.Firefox) familiesOk = false;
            Check(scanOk && familiesOk, "browser scan unique existing files by family");

            string neat = "1:GET\r\n2:https://example.com/a/b.mp4?x=1\r\n6:normal\r\n"
                        + "4:MP4 File 2 MB\r\n7:1234\r\n8:video/mp4\r\n"
                        + "Referer: https://example.com/page\r\n"
                        + "User-Agent: Test Browser/1.0\r\n"
                        + "Cookie: a=1; b=2\r\n9:1\r\n10:pair123\r\nContent-Disposition: attachment; filename=\"clip.mp4\"\r\n";
            NeatRequest nr = NeatLink.Parse(neat);
            Check(nr != null && nr.Url == "https://example.com/a/b.mp4?x=1"
                  && nr.Cookie == "a=1; b=2" && nr.FileName == "clip.mp4"
                  && nr.Size == 1234 && nr.ContentType == "video/mp4"
                  && nr.Referer == "https://example.com/page"
                  && nr.UserAgent == "Test Browser/1.0"
                  && nr.Auto && nr.Pair == "pair123" && nr.Kind == "normal", "neat parse");
            Dictionary<string, DateTime> neatSeen = new Dictionary<string, DateTime>();
            DateTime neatT0 = new DateTime(2026, 1, 1, 0, 0, 0);
            Check(!NeatLink.RecentlySeen(neatSeen, "http://x/a", neatT0, 5), "neat dedupe first");
            Check(NeatLink.RecentlySeen(neatSeen, "http://x/a/", neatT0.AddSeconds(1), 5),
                  "neat dedupe same url");
            Check(!NeatLink.RecentlySeen(neatSeen, "http://x/b", neatT0.AddSeconds(1), 5),
                  "neat dedupe other url");
            Check(!NeatLink.RecentlySeen(neatSeen, "http://x/a", neatT0.AddSeconds(6), 5),
                  "neat dedupe window");
            Check(NeatLink.WaitsForBetter("normal") && !NeatLink.WaitsForBetter("file")
                  && !NeatLink.WaitsForBetter("video"),
                  "neat normal waits for named file");
            Check(Util.HostOf("https://S1-FileCR.xyz/a.zip?x=1") == "s1-filecr.xyz", "host of url");
            Check(Util.NormalizeHosts(" example.com, *.Foo.com ; bad/path , example.com ")
                  == "example.com, foo.com", "blocked hosts normalize");
            Check(Util.IsBlockedHost("filecr.xyz", "s1.filecr.xyz")
                  && !Util.IsBlockedHost("filecr.xyz", "notfilecr.xyz")
                  && Util.IsBlockedHost("filecr.xyz", "filecr.xyz"), "blocked host match");
            Check(Util.AddBlockedHost("a.com", "A.com") == "a.com", "blocked host no duplicate");
            Check(Util.AddBlockedHost("", "b.com") == "b.com", "blocked host first");
            Check(Aria.YouTubeId("https://www.youtube.com/watch?v=abc123") == "abc123", "youtube id watch");
            Check(Aria.YouTubeId("https://www.youtube.com/watch?si=1&v=abc123") == "abc123", "youtube id query order");
            Check(Aria.YouTubeId("https://youtu.be/abc123?t=1") == "abc123", "youtube id short url");
            Check(Aria.YouTubeId("https://www.youtube.com/shorts/abc123") == "abc123", "youtube id shorts");
            Check(Aria.YouTubeId("https://example.com/watch?v=abc123") == "", "youtube id other host");
            Check(Aria.YouTubeId("https://www.youtube.com/watch?v=abc!") == "", "youtube id invalid chars");
            Check(Aria.MergeBaseName("Title - 1080p.mp4") == "Title", "merge name quality");
            Check(Aria.MergeBaseName("Title - 1080p60 HDR.mp4") == "Title", "merge name hdr");
            Check(Aria.MergeBaseName("Title - 1080p Premium.mp4") == "Title", "merge name premium");
            Check(Aria.MergeBaseName("Title - itag 137.mp4") == "Title", "merge name itag");
            Check(Aria.MergeBaseName("Title - 129kbps.m4a") == "Title", "merge name bitrate");
            Check(Aria.MergeBaseName("My - Trip.mp4") == "My - Trip", "merge name keeps words");
            Check(Aria.MergeBaseName("Title.mp4") == "Title", "merge name plain");

            string mxErr;
            string mxOut = Path.Combine(Path.GetTempPath(), "zid_mux_test.mp4");
            Check(!Mp4Mux.Merge("missing.mp4", "missing.m4a", mxOut, out mxErr)
                  && mxErr.Length > 0, "mux rejects missing input");
            string tv = Path.Combine(Path.GetTempPath(), "opencode\\yt\\v.mp4");
            string ta = Path.Combine(Path.GetTempPath(), "opencode\\yt\\a.m4a");
            if (File.Exists(tv) && File.Exists(ta))
            {
                bool hv, ha;
                Check(Mp4Mux.TryReadTrackKinds(tv, out hv, out ha) && hv && !ha,
                      "mux reads video-only header");
                Check(Mp4Mux.TryReadTrackKinds(ta, out hv, out ha) && !hv && ha,
                      "mux reads audio-only header");
                Check(Mp4Mux.Merge(tv, ta, mxOut, out mxErr) &&
                      File.Exists(mxOut) && new FileInfo(mxOut).Length > 1000,
                      "mux fragmented mp4 + m4a");
                Check(Mp4Mux.TryReadTrackKinds(mxOut, out hv, out ha) && hv && ha,
                      "mux reads merged header");
                try { File.Delete(mxOut); } catch { }
            }
            NeatRequest nr5 = NeatLink.Parse("1:GET\r\n2:https://example.com/x.mp4\r\n5:https://example.com/page\r\n");
            Check(nr5 != null && nr5.Referer == "https://example.com/page", "neat parse referer key 5");
            NeatRequest nrsub = NeatLink.Parse(
                "1:GET\r\n2:https://www.youtube.com/api/timedtext?v=abc&fmt=vtt\r\n" +
                "6:subtitle\r\n9:1\r\n10:pabc\r\n" +
                "Content-Disposition: attachment; filename=\"Clip.en.vtt\"\r\n");
            Check(nrsub != null && nrsub.Kind == "subtitle" && nrsub.Auto &&
                  nrsub.Pair == "pabc" && nrsub.FileName == "Clip.en.vtt",
                  "neat parse subtitle auto pair");
            Check(Aria.TypeFolder("VTT") == "Video" && Aria.TypeFolder("srt") == "Video"
                  && Aria.TypeFolder("mp4") == "Video" && Aria.TypeFolder("mp3") == "Audio",
                  "autosort puts subtitles with video");

            Check(Util.IsBlank(null) && Util.IsBlank(" \t") && !Util.IsBlank("x"), "util blank");
            Check(Util.NormalizeHeaders(" a: 1\r\nb: 2\r\n ") == "a: 1\nb: 2", "util headers");
            Check(Util.NormalizeHeaders(new string('x', 9000)).Length == 8192, "util headers cap");
            Check(Util.NormalizeChecksum(" sha-256=abc ") == "sha-256=abc", "util checksum");
            Check(Util.NormalizeChecksum("abc") == "" && Util.NormalizeChecksum("sha 256=abc") == "",
                  "util checksum rejects malformed");
            Check(Util.NormalizeMirror(" https://m.example/a ", "https://e.example/a") == "https://m.example/a",
                  "util mirror");
            Check(Util.NormalizeMirror("https://e.example/a", "HTTPS://E.EXAMPLE/A") == "",
                  "util mirror same as primary");
            Check(Util.NormalizeMirror("javascript:alert(1)", "") == "" &&
                  Util.NormalizeMirror("not a url", "") == "", "util mirror rejects scheme");
            Check(Util.SanitizeFileName(null) == "download.bin" &&
                  Util.SanitizeFileName("") == "download.bin", "util filename empty");
            Check(Util.SanitizeFileName(@"..\evil.mp4") == "evil.mp4", "util filename strips folders");
            Check(Util.SanitizeFileName("a?b*.mp4") == "a_b_.mp4", "util filename bad chars");
            Check(Util.SanitizeFileName("..") == "download.bin" &&
                  Util.SanitizeFileName(".") == "download.bin", "util filename traversal");
            Check(Util.SanitizeFileName("name. . ") == "name", "util filename trailing dots");
            Check(Util.SanitizeFileName("CON") == "_CON" &&
                  Util.SanitizeFileName("con.txt") == "_con.txt" &&
                  Util.SanitizeFileName("LPT9.tar.gz") == "_LPT9.tar.gz" &&
                  Util.SanitizeFileName("COM10") == "COM10", "util filename reserved names");
            Check(Util.SanitizeFileName(@"C:\temp\x.txt") == "x.txt", "util filename absolute path");
            string longName = Util.SanitizeFileName(new string('a', 300) + ".mp4");
            Check(longName.Length <= 200 && longName.EndsWith(".mp4", StringComparison.Ordinal),
                  "util filename long path");
            Check(Util.FormatEta(0) == "00:00:00" && Util.FormatEta(3661) == "01:01:01", "util eta");
            Check(Util.Csv("a\"b") == "\"a\"\"b\"", "util csv");
            Check(Util.NormalizeUrl("HTTPS://Example.COM:443/a/b/?q=1#frag") ==
                  "https://example.com/a/b?q=1", "util url normalize");
            Check(Util.SameUrl("http://x:80/a", "http://X/a/") &&
                  !Util.SameUrl("http://x/a?b=1", "http://x/a?b=2"), "util same url");
            Check(Util.NormalizeUrl("javascript:alert(1)") == "" &&
                  Util.NormalizeUrl("nope") == "", "util url rejects scheme");
            Check(Util.NormalizeUrl("ftp://x:21/a/") == "ftp://x/a", "util ftp default port");
            Check(Util.AllowDocumentNavigation("about:blank") &&
                  Util.AllowDocumentNavigation("") &&
                  Util.AllowDocumentNavigation("res://ieframe.dll/error.htm") &&
                  !Util.AllowDocumentNavigation("file:///C:/evil.html") &&
                  !Util.AllowDocumentNavigation("https://evil.example/") &&
                  !Util.AllowDocumentNavigation("javascript:alert(1)"),
                  "embedded page cannot navigate away");
            Check(Util.MaskValue("") == "" && Util.MaskValue(null) == "" &&
                  Util.MaskValue("cookie=1") == "********" &&
                  Util.MaskValue("cookie=1").IndexOf("cookie") < 0,
                  "info masks secrets");

            Check(MetadataProbe.FileNameFromDisposition("attachment; filename=\"setup.exe\"") == "setup.exe" &&
                  MetadataProbe.FileNameFromDisposition("attachment; filename=my file.zip") == "my file.zip" &&
                  MetadataProbe.FileNameFromDisposition("attachment; filename*=UTF-8''my%20file.mp4") == "my file.mp4" &&
                  MetadataProbe.FileNameFromDisposition("attachment; filename=\"../x.txt\"") == "x.txt" &&
                  MetadataProbe.FileNameFromDisposition("inline") == "" &&
                  MetadataProbe.FileNameFromDisposition(null) == "",
                  "probe reads content-disposition");
            Check(MetadataProbe.Probe("javascript:alert(1)") == "" &&
                  MetadataProbe.Probe("ftp://example.com/a.bin") == "",
                  "probe only takes http(s)");

            Check(AddService.IsSupportedUrl("http://x/a") &&
                  AddService.IsSupportedUrl("https://x/a") &&
                  AddService.IsSupportedUrl("ftp://x/a") &&
                  !AddService.IsSupportedUrl("file:///c:/a") &&
                  !AddService.IsSupportedUrl("not a url") &&
                  !AddService.IsSupportedUrl(null) &&
                  !AddService.IsSupportedUrl(""), "add rejects unsupported urls");
            Check(AddService.FileName("https://x/dir/movie.mp4?token=1", null, 9) == "movie.mp4" &&
                  AddService.FileName("https://x/dir/movie.mp4", "", 9) == "movie.mp4",
                  "add picks the url file name");
            Check(AddService.FileName("https://x/a", @"..\evil.mp4", 9) == "evil.mp4" &&
                  AddService.FileName("https://x/a", "chosen.bin", 9) == "chosen.bin",
                  "add keeps the requested file name");
            Check(AddService.FileName("https://x/", null, 7) == "download_7.bin" &&
                  AddService.FileName(null, null, 8) == "download_8.bin",
                  "add falls back to the id name");
            Check(AddService.TypeFor("a.MP4") == "MP4" &&
                  AddService.TypeFor("README") == "FILE", "add derives the type");

            DownloadItem addItem = AddService.CreateItem(5, "https://x/a.mp4", @"C:\dl\a.mp4", "MP4",
                "c=1", " https://www.youtube.com/watch?v=abc123 ", " UA ", " VIDEO ", true, "",
                " a: 1\r\nb: 2\r\n ", " sha-256=abc ", "https://mirror/a.mp4", " proxy:1 ",
                "8", "250");
            Check(addItem.Id == 5 && addItem.FileName == "a.mp4" &&
                  addItem.LocalPath == @"C:\dl\a.mp4" && addItem.Url == "https://x/a.mp4" &&
                  addItem.Cookie == "c=1" &&
                  addItem.Referer == "https://www.youtube.com/watch?v=abc123" &&
                  addItem.UserAgent == "UA" && addItem.Kind == "video" && addItem.Auto &&
                  addItem.Type == "MP4" && addItem.MaxConn == 8 && addItem.SpeedKbps == 250 &&
                  addItem.Headers == "a: 1\nb: 2" && addItem.Checksum == "sha-256=abc" &&
                  addItem.Mirror == "https://mirror/a.mp4" && addItem.Proxy == "proxy:1" &&
                  addItem.MergeKey == "abc123", "add maps the request fields");

            DownloadItem addBlank = AddService.CreateItem(6, "https://x/b.bin", @"C:\dl\b.bin",
                "FILE", "", "  ", null, "", false, " ", "", "", "https://x/b.bin", " ",
                "0", "-1");
            Check(addBlank.Cookie == "" && addBlank.Referer == "" && addBlank.UserAgent == "" &&
                  addBlank.Kind == "" && addBlank.Proxy == "" && addBlank.MergeKey == "" &&
                  addBlank.Mirror == "" && addBlank.MaxConn == 0 && addBlank.SpeedKbps == 0 &&
                  !addBlank.Auto, "add clears blank options");

            DownloadItem addClamp = AddService.CreateItem(7, "https://x/c", @"C:\dl\c", "FILE",
                "", "", "", "", false, " k ", "", "", "", "", "17", "-5");
            DownloadItem addMax = AddService.CreateItem(8, "https://x/d", @"C:\dl\d", "FILE",
                "", "", "", "", false, "", "", "", "", "", "16", "0");
            Check(addClamp.MaxConn == 0 && addClamp.SpeedKbps == 0 && addClamp.MergeKey == "k" &&
                  addMax.MaxConn == 16 && addMax.SpeedKbps == 0, "add clamps bad options");

            string addDir = Path.Combine(Path.GetTempPath(), "zid_add_" + Guid.NewGuid().ToString("N"));
            string addSorted = AddService.PrepareFolder(addDir, "", true, "MP4");
            string addChosen = Path.Combine(addDir, "chosen");
            Check(addSorted == Path.Combine(addDir, "Video") && Directory.Exists(addSorted) &&
                  AddService.PrepareFolder(addDir, "", false, "MP4") == addDir &&
                  AddService.PrepareFolder(addDir, addChosen, true, "MP4") == addChosen &&
                  Directory.Exists(addChosen), "add prepares the save folder");

            string tplName = "movie.mp4";
            string tplFolder = AddService.TemplateFolder(addDir, "{host}/{type}/{name}",
                "https://example.com/dir/movie.mp4?x=1", "MP4", ref tplName);
            Check(tplFolder == Path.Combine(Path.Combine(addDir, "example.com"), "Video") &&
                  tplName == "movie.mp4" && Directory.Exists(tplFolder),
                  "add template keeps host, type and name");
            string tplKeep = "clip.mkv";
            string tplHost = AddService.TemplateFolder(addDir, "{host}",
                "https://example.com/a", "MP4", ref tplKeep);
            Check(tplHost == Path.Combine(addDir, "example.com") && tplKeep == "clip.mkv",
                  "add template without a name is a folder");
            string tplExt = "archive.tar.gz";
            string tplFile = AddService.TemplateFolder(addDir, "{type}/{name}",
                "https://x/a", "MP4", ref tplExt);
            Check(tplFile == Path.Combine(addDir, "Video") &&
                  tplExt == "archive.tar.gz", "add template keeps a double extension");

            File.WriteAllText(Path.Combine(addDir, "a.txt"), "x");
            Check(AddService.UniquePath(addDir, "a.txt") == Path.Combine(addDir, "a (1).txt"),
                  "add renames a file collision");
            File.WriteAllText(Path.Combine(addDir, "a (1).txt"), "x");
            Check(AddService.UniquePath(addDir, "a.txt") == Path.Combine(addDir, "a (2).txt"),
                  "add renames a second collision");
            Check(AddService.UniquePath(addDir, "b.txt", delegate(string p)
                  {
                      return p == Path.Combine(addDir, "b.txt") ||
                             p == Path.Combine(addDir, "b (1).txt");
                  }) == Path.Combine(addDir, "b (2).txt"),
                  "add skips paths already owned by a row");

            Check(AddService.HlsOutputPath(addDir, "") == Path.Combine(addDir, "video.mp4") &&
                  AddService.HlsOutputPath(addDir, "clip.webm") == Path.Combine(addDir, "clip.mp4"),
                  "add names the hls output");
            File.WriteAllText(Path.Combine(addDir, "clip.mp4"), "x");
            Check(AddService.HlsOutputPath(addDir, "clip.webm") ==
                  Path.Combine(addDir, "clip (1).mp4"), "add renames an hls collision");
            Check(AddService.HlsOutputPath(addDir, "clip2.webm", delegate(string p)
                  {
                      return p == Path.Combine(addDir, "clip2.mp4");
                  }) == Path.Combine(addDir, "clip2 (1).mp4"),
                  "add skips an hls path owned by a row");
            try { Directory.Delete(addDir, true); } catch { }

            // aria2 gets the shipped Mozilla roots plus the machine's trusted
            // roots, so an HTTPS-scanning proxy cannot break every download.
            string cbDir = Path.Combine(Path.GetTempPath(), "opencode\\zid_ca_test");
            try { Directory.CreateDirectory(cbDir); } catch { }
            string cbShipped = Path.Combine(cbDir, "ca-certificates.crt");
            File.WriteAllText(cbShipped,
                "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----\r\n");
            try { File.Delete(Path.Combine(cbDir, "ca-bundle.crt")); } catch { }
            string cb = CaBundle.Prepare(cbDir, cbDir);
            Check(File.Exists(cb) && File.ReadAllText(cb).IndexOf("BEGIN CERTIFICATE") >= 0
                  && File.ReadAllText(cb).Length >= File.ReadAllText(cbShipped).Length,
                  "ca bundle keeps the shipped roots");
            Check(NeatLink.Parse("1:POST\r\n2:https://example.com/x\r\n") == null, "neat parse skips POST");
            Check(NeatLink.Parse("1:GET\r\n2:javascript:alert(1)\r\n") == null, "neat parse rejects scheme");
            NeatRequest neatTake = NeatLink.Parse(
                "1:GET\r\n2:https://example.com/setup.zip\r\n6:file\r\n4:setup.zip\r\n9:1\r\n"
                + "Content-Disposition: attachment; filename=\"setup.zip\"\r\n");
            Check(NeatLink.Reply(null) == "err" && NeatLink.Reply(neatTake) == "ok" &&
                  NeatLink.Reply(true) == "ok" && NeatLink.Reply(false) == "err" &&
                  neatTake.Auto && neatTake.Kind == "file" && neatTake.FileName == "setup.zip",
                  "neat ack format");
            Check(NeatLink.VersionQuery == "zid-version" && NeatLink.AppVersion().Length > 0,
                  "neat version handshake");
            Check(NeatLink.AcceptKey("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
                  "neat websocket accept key");
            Check(NeatLink.AllowedOrigin(null) && NeatLink.AllowedOrigin("")
                  && NeatLink.AllowedOrigin("chrome-extension://abcdefgh")
                  && NeatLink.AllowedOrigin("moz-extension://1111-2222/")
                  && !NeatLink.AllowedOrigin("https://evil.example")
                  && !NeatLink.AllowedOrigin("http://127.0.0.1:8000")
                  && !NeatLink.AllowedOrigin("null"), "neat origin gate");

            string wsReq = "GET /download HTTP/1.1\r\nHost: 127.0.0.1:10007\r\n"
                         + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                         + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
                         + "Sec-WebSocket-Version: 13\r\n"
                         + "Sec-WebSocket-Protocol: neatextension.v1\r\n"
                         + "Origin: chrome-extension://abcdef\r\n\r\n";
            bool wsAuth;
            Check(NeatLinkServer.Handshake(Req(wsReq), "SECRET", out wsAuth) && !wsAuth,
                  "ws handshake extension origin");
            Check(!NeatLinkServer.Handshake(Req(
                  wsReq.Replace("chrome-extension://abcdef", "https://evil.example")),
                  "SECRET", out wsAuth), "ws handshake rejects web origin");
            Check(NeatLinkServer.Handshake(Req(
                  wsReq.Replace("Origin: chrome-extension://abcdef", "")
                       .Replace("neatextension.v1", "token.SECRET")),
                  "SECRET", out wsAuth) && wsAuth, "ws handshake token auth");
            Check(!NeatLinkServer.Handshake(Req(
                  wsReq.Replace("Origin: chrome-extension://abcdef", "")),
                  "SECRET", out wsAuth), "ws handshake rejects a local tool");
            Check(!NeatLinkServer.Handshake(Req(
                  wsReq.Replace("Sec-WebSocket-Protocol: neatextension.v1", "")),
                  "SECRET", out wsAuth), "ws handshake needs the extension protocol");
            Check(NeatLink.TokenMatches("neatextension.v1, token.SECRET", "SECRET") &&
                  !NeatLink.TokenMatches("neatextension.v1", "SECRET") &&
                  !NeatLink.TokenMatches("token.SECRET", ""), "ws token match rules");

            int rateStart = 0, rateCount = 0;
            Check(NeatLink.RateAllow(ref rateStart, ref rateCount, 1000, 2, 10000) &&
                  NeatLink.RateAllow(ref rateStart, ref rateCount, 1500, 2, 10000) &&
                  !NeatLink.RateAllow(ref rateStart, ref rateCount, 2000, 2, 10000) &&
                  NeatLink.RateAllow(ref rateStart, ref rateCount, 12000, 2, 10000),
                  "ws message rate window");

            string statePath = Path.Combine(Path.GetTempPath(), "zid_state_test.json");
            try { File.Delete(statePath); } catch { }
            DownloadItem src = new DownloadItem();
            src.Id = 7;
            src.FileName = "clip.mp4";
            src.Url = "https://example.com/a?b=1&c=2";
            src.Cookie = "a=1; b=2";
            src.Referer = "https://example.com/page";
            src.UserAgent = "Test/1.0";
            src.Headers = "Origin: https://example.com\nX-Test: 1";
            src.Checksum = "sha-256=abc123";
            src.Mirror = "https://mirror.example.com/clip.mp4";
            src.Proxy = "http://user:pass@proxy:8080";
            src.Type = "MP4";
            src.Size = 123456789;
            src.Progress = 42;
            src.Status = "Done";
            src.LocalPath = @"C:\Downloads\clip.mp4";
            src.BytesReceived = 100;
            src.MaxConn = 8;
            src.SpeedKbps = 512;
            src.AfterDone = "open";
            src.Priority = 1;
            src.Kind = "video";
            src.MergeKey = "abc123";
            src.Merged = true;
            src.Auto = true;
            src.ResumeOnLoad = true;
            src.RetryCount = 3;
            src.Date = new DateTime(2026, 10, 27, 12, 34, 0);
            src.StartAt = new DateTime(2026, 10, 28, 9, 0, 0);
            List<DownloadItem> stateItems = new List<DownloadItem>();
            stateItems.Add(src);
            StateStore stateStore = new StateStore(statePath);
            stateStore.Save(stateItems, 8, null);
            StateData stateBack = stateStore.Load(null);
            bool roundTrip = stateBack.Items.Count == 1 && stateBack.NextId == 8;
            if (roundTrip)
            {
                DownloadItem d = stateBack.Items[0];
                roundTrip = d.Id == 7 && d.FileName == "clip.mp4" && d.Url == src.Url
                    && d.Cookie == src.Cookie && d.Referer == src.Referer
                    && d.UserAgent == src.UserAgent && d.Headers == src.Headers
                    && d.Checksum == src.Checksum && d.Mirror == src.Mirror
                    && d.Proxy == src.Proxy
                    && d.Type == "MP4"
                    && d.Size == 123456789 && d.Progress == 42 && d.Status == "Done"
                    && d.LocalPath == src.LocalPath && d.BytesReceived == 100
                    && d.MaxConn == 8 && d.SpeedKbps == 512 && d.AfterDone == "open"
                    && d.Priority == 1 && d.Kind == "video" && d.MergeKey == "abc123"
                    && d.Merged && d.Auto && d.ResumeOnLoad && d.Date == src.Date
                    && d.StartAt == src.StartAt
                    && d.RetryCount == 3;
            }
            try { File.Delete(statePath); } catch { }
            Check(roundTrip, "state round trip all fields");

            // Atomic save keeps one backup, and load must never silently start empty.
            string safePath = Path.Combine(Path.GetTempPath(), "zid_state_safe_test.json");
            try { File.Delete(safePath); } catch { }
            try { File.Delete(safePath + ".bak"); } catch { }
            try { File.Delete(safePath + ".tmp"); } catch { }
            List<string> stateLog = new List<string>();
            StateLog capture = delegate(string m) { stateLog.Add(m); };
            StateStore safeStore = new StateStore(safePath);
            DownloadItem safeItem = new DownloadItem();
            safeItem.Id = 1;
            safeItem.FileName = "first.bin";
            safeItem.Status = "Done";
            List<DownloadItem> safeItems = new List<DownloadItem>();
            safeItems.Add(safeItem);
            safeStore.Save(safeItems, 2, capture);
            Check(File.Exists(safePath) && !File.Exists(safePath + ".bak") &&
                  File.ReadAllText(safePath).IndexOf("\"schemaVersion\":1") >= 0,
                  "state save writes schema version");
            safeItem.FileName = "second.bin";
            safeStore.Save(safeItems, 2, capture);
            Check(File.Exists(safePath + ".bak"), "state save keeps backup");
            File.WriteAllText(safePath, "{broken");
            StateData restored = safeStore.Load(capture);
            Check(restored.Items.Count == 1 && restored.Items[0].FileName == "first.bin" &&
                  stateLog.Count > 0, "state corrupt main falls back to backup");
            File.Delete(safePath);
            restored = safeStore.Load(capture);
            Check(restored.Items.Count == 1 && restored.Items[0].FileName == "first.bin",
                  "state missing main falls back to backup");
            File.WriteAllText(safePath, "{broken");
            File.WriteAllText(safePath + ".bak", "{broken");
            restored = safeStore.Load(capture);
            Check(restored.Items.Count == 0, "state corrupt both starts empty");
            File.WriteAllText(safePath, "{\"nextId\":5,\"items\":[]}");
            restored = safeStore.Load(capture);
            Check(restored.NextId == 5 && restored.Items.Count == 0, "state loads file without schema version");
            try { File.Delete(safePath); } catch { }
            try { File.Delete(safePath + ".bak"); } catch { }
            try { File.Delete(safePath + ".tmp"); } catch { }

            // T1.2: capture is an immutable snapshot; the writer gets a string.
            string capPath = Path.Combine(Path.GetTempPath(), "zid_state_capture_test.json");
            try { File.Delete(capPath); } catch { }
            try { File.Delete(capPath + ".bak"); } catch { }
            try { File.Delete(capPath + ".tmp"); } catch { }
            StateStore capStore = new StateStore(capPath);
            string capturedJson = capStore.Capture(stateItems, 8);
            src.FileName = "changed-after-capture.bin";
            capStore.SaveCaptured(capturedJson, capture);
            StateData capBack = capStore.Load(null);
            Check(capturedJson.IndexOf("\"clip.mp4\"") >= 0 &&
                  capturedJson.IndexOf("changed-after-capture.bin") < 0 &&
                  capBack.Items.Count == 1 && capBack.Items[0].FileName == "clip.mp4" &&
                  capBack.NextId == 8, "state capture is an immutable snapshot");

            // Coalescing: while the first write is blocked, two newer saves
            // collapse into one, so only the newest snapshot reaches disk.
            ManualResetEvent writeEntered = new ManualResetEvent(false);
            ManualResetEvent releaseWrite = new ManualResetEvent(false);
            int writes = 0;
            string lastWritten = null;
            StateWrite slowWrite = delegate(string json, StateLog log)
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    writeEntered.Set();
                    releaseWrite.WaitOne(5000);
                }
                lastWritten = json;
            };
            StateWriter coalescing = new StateWriter(slowWrite, capture);
            coalescing.Save("a");
            bool entered = writeEntered.WaitOne(5000);
            coalescing.Save("b");
            coalescing.Save("c");
            releaseWrite.Set();
            bool coalesced = coalescing.Flush(5000);
            Check(entered && coalesced && writes == 2 && lastWritten == "c",
                  "state writer coalesces to newest snapshot");

            StateWriter idle = new StateWriter(delegate(string json, StateLog log) { },
                                               capture);
            Check(idle.Flush(50), "state writer flush with nothing pending");

            List<string> failLog = new List<string>();
            StateLog failCapture = delegate(string m) { failLog.Add(m); };
            StateWriter failing = new StateWriter(delegate(string json, StateLog log)
            {
                throw new InvalidOperationException("boom");
            }, failCapture);
            failing.Save("x");
            Check(failing.Flush(5000) && failLog.Count > 0,
                  "state writer flush after write failure");

            try { File.Delete(capPath); } catch { }
            try { File.Delete(capPath + ".bak"); } catch { }
            try { File.Delete(capPath + ".tmp"); } catch { }

            Check(DownloadStates.Parse("Done") == DownloadState.Completed &&
                  DownloadStates.Parse("Error") == DownloadState.Failed &&
                  DownloadStates.Parse("Connecting") == DownloadState.Starting,
                  "state parse legacy status");
            Check(DownloadStates.ToStatus(DownloadState.Completed) == "Done" &&
                  DownloadStates.ToStatus(DownloadState.Failed) == "Error" &&
                  DownloadStates.ToStatus(DownloadState.Starting) == "Connecting",
                  "state format legacy status");
            Check(DownloadStates.Parse(null) == DownloadState.Paused &&
                  DownloadStates.Parse("nonsense") == DownloadState.Paused, "state unknown is paused");
            Check(DownloadStates.IsActive(DownloadState.Queued) &&
                  DownloadStates.IsActive(DownloadState.Starting) &&
                  DownloadStates.IsActive(DownloadState.Downloading) &&
                  !DownloadStates.IsActive(DownloadState.Completed), "state is active");
            Check(DownloadStates.CanTransition(DownloadState.Queued, DownloadState.Starting) &&
                  DownloadStates.CanTransition(DownloadState.Starting, DownloadState.Downloading) &&
                  DownloadStates.CanTransition(DownloadState.Downloading, DownloadState.Completed) &&
                  DownloadStates.CanTransition(DownloadState.Paused, DownloadState.Paused) &&
                  !DownloadStates.CanTransition(DownloadState.Completed, DownloadState.Downloading) &&
                  !DownloadStates.CanTransition(DownloadState.Removed, DownloadState.Queued),
                  "state transitions");
            Check(DownloadStates.CanTransition(DownloadState.Queued, DownloadState.Waiting) &&
                  DownloadStates.CanTransition(DownloadState.Downloading, DownloadState.Starting) &&
                  DownloadStates.CanTransition(DownloadState.Downloading, DownloadState.Queued) &&
                  DownloadStates.CanTransition(DownloadState.Completed, DownloadState.Starting) &&
                  DownloadStates.CanTransition(DownloadState.Completed, DownloadState.Merging) &&
                  DownloadStates.CanTransition(DownloadState.Completed, DownloadState.Paused) &&
                  DownloadStates.CanTransition(DownloadState.Failed, DownloadState.Paused),
                  "state transitions for retry/edit/restart");
            Check(DownloadStates.Parse("WaitingNetwork") == DownloadState.WaitingNetwork &&
                  DownloadStates.ToStatus(DownloadState.WaitingNetwork) == "WaitingNetwork",
                  "state waiting network round trip");
            Check(DownloadStates.CanTransition(DownloadState.Downloading, DownloadState.WaitingNetwork) &&
                  DownloadStates.CanTransition(DownloadState.Starting, DownloadState.WaitingNetwork) &&
                  DownloadStates.CanTransition(DownloadState.WaitingNetwork, DownloadState.Starting) &&
                  DownloadStates.CanTransition(DownloadState.WaitingNetwork, DownloadState.Queued) &&
                  DownloadStates.CanTransition(DownloadState.WaitingNetwork, DownloadState.Paused) &&
                  DownloadStates.CanTransition(DownloadState.WaitingNetwork, DownloadState.Failed) &&
                  !DownloadStates.CanTransition(DownloadState.WaitingNetwork, DownloadState.Completed) &&
                  !DownloadStates.IsActive(DownloadState.WaitingNetwork),
                  "state waiting network transitions");
            Check(NetworkMonitor.ShouldWaitForNetwork(false, EngineErrorClass.Network) &&
                  NetworkMonitor.ShouldWaitForNetwork(false, EngineErrorClass.Transient) &&
                  NetworkMonitor.ShouldWaitForNetwork(false, EngineErrorClass.Unknown) &&
                  !NetworkMonitor.ShouldWaitForNetwork(false, EngineErrorClass.Permanent) &&
                  !NetworkMonitor.ShouldWaitForNetwork(false, EngineErrorClass.Local) &&
                  !NetworkMonitor.ShouldWaitForNetwork(true, EngineErrorClass.Network),
                  "network wait policy");
            Check(NetworkMonitor.StaggerMs(0, 0) == 0 &&
                  NetworkMonitor.StaggerMs(1, 50) == 300 &&
                  NetworkMonitor.StaggerMs(100, 0) == 5000 &&
                  NetworkMonitor.StaggerMs(-1, -1) == 0,
                  "network stagger bounds");
            DownloadItem stateItem = new DownloadItem();
            stateItem.State = DownloadState.Completed;
            Check(stateItem.Status == "Done" && stateItem.State == DownloadState.Completed,
                  "download item state property");

            string recDir = Path.Combine(Path.GetTempPath(), "zid_recovery_test");
            try { Directory.CreateDirectory(recDir); } catch { }
            string recFile = Path.Combine(recDir, "ok.bin");
            File.WriteAllText(recFile, "x");
            List<DownloadItem> recItems = new List<DownloadItem>();
            DownloadItem recOk = new DownloadItem();
            recOk.Id = 1; recOk.FileName = "ok.bin"; recOk.Url = "https://example.com/ok.bin";
            recOk.LocalPath = recFile; recOk.Status = "Done"; recOk.Eta = "Done";
            DownloadItem recGone = new DownloadItem();
            recGone.Id = 2; recGone.FileName = "gone.bin"; recGone.Url = "https://example.com/gone.bin";
            recGone.LocalPath = Path.Combine(recDir, "gone.bin"); recGone.Status = "Done";
            DownloadItem recBad = new DownloadItem();
            recBad.Id = 3; recBad.FileName = "x.bin"; recBad.Url = "javascript:alert(1)";
            recBad.Status = "Queued"; recBad.ResumeOnLoad = true;
            DownloadItem recBlank = new DownloadItem();
            recBlank.Url = "https://example.com/a.bin"; recBlank.Status = "Paused";
            DownloadItem recCrash = new DownloadItem();
            recCrash.Id = 5; recCrash.FileName = "crash.bin";
            recCrash.Url = "https://example.com/crash.bin"; recCrash.Status = "Downloading";
            DownloadItem recNet = new DownloadItem();
            recNet.Id = 6; recNet.FileName = "net.bin";
            recNet.Url = "https://example.com/net.bin"; recNet.Status = "WaitingNetwork";
            recItems.Add(recOk); recItems.Add(recGone); recItems.Add(recBad);
            recItems.Add(recBlank); recItems.Add(recCrash); recItems.Add(recNet);
            int recNext = Recovery.Validate(recItems, recDir, 3, null);
            Check(recCrash.ResumeOnLoad && !recBlank.ResumeOnLoad,
                  "recovery resumes a crashed active row but not a paused one");
            Check(recNet.ResumeOnLoad && recNet.State == DownloadState.WaitingNetwork,
                  "recovery resumes a waiting-network row");
            Check(recOk.Eta == "Done", "recovery keeps existing completed file");
            Check(recGone.Eta == "File moved or deleted" && recGone.AfterDoneRan,
                  "recovery flags missing completed file");
            Check(recBad.State == DownloadState.Failed && !recBad.ResumeOnLoad &&
                  recBad.Eta.IndexOf("invalid URL") >= 0, "recovery fails invalid URL");
            Check(recBlank.Id > 0 && recBlank.FileName.Length > 0 &&
                  recBlank.LocalPath.StartsWith(recDir, StringComparison.OrdinalIgnoreCase),
                  "recovery fills missing id/name/path");
            Check(recNext > 3, "recovery advances next id");

            DownloadItem miss = new DownloadItem();
            miss.Id = 4; miss.State = DownloadState.Completed; miss.Eta = "Done";
            miss.LocalPath = Path.Combine(recDir, "missing.bin");
            Check(Recovery.MarkMissing(miss, null) && miss.Eta == Recovery.MissingText &&
                  miss.AfterDoneRan, "history marks missing file");
            Check(!Recovery.MarkMissing(miss, null), "history missing marker is stable");
            Check(!Recovery.MarkMissing(recOk, null) && recOk.Eta == "Done",
                  "history keeps existing file");
            miss.LocalPath = recFile;
            Check(Recovery.MarkMissing(miss, null) && miss.Eta == "Done",
                  "history clears marker when file returns");

            try { File.Delete(recFile); } catch { }
            try { Directory.Delete(recDir, true); } catch { }

            DownloadManager dm = new DownloadManager();
            dm.NextId = 5;
            DownloadItem dm1 = new DownloadItem(); dm1.Id = 1; dm1.Status = "Queued";
            DownloadItem dm2 = new DownloadItem(); dm2.Id = 2; dm2.Status = "Done";
            DownloadItem dm3 = new DownloadItem(); dm3.Id = 3; dm3.Status = "Done";
            List<DownloadItem> dmExtra = new List<DownloadItem>(); dmExtra.Add(dm2); dmExtra.Add(dm3);
            dm.Add(dm1); dm.AddRange(dmExtra);
            Check(dm.TakeNextId() == 5 && dm.NextId == 6, "manager next id");
            Check(dm.Count == 3 && dm.Find(2) == dm2 && dm.Snapshot().Count == 3, "manager add/find/snapshot");
            Check(dm.CountWhere(delegate(DownloadItem x) { return x.State == DownloadState.Completed; }) == 2,
                  "manager count where");
            dm.RemoveById(1);
            Check(dm.Find(1) == null && dm.Count == 2 && dm1.State == DownloadState.Removed,
                  "manager remove by id");
            dm.Remove(dm2);
            Check(dm.Count == 1 && dm.Find(3) == dm3 && dm2.State == DownloadState.Removed,
                  "manager remove item");
            Check(dm.RemoveWhere(delegate(DownloadItem x) { return x.Id == 3; }) == 1 && dm.Count == 0,
                  "manager remove where");

            DownloadManager life = new DownloadManager();
            DownloadItem li = new DownloadItem();
            li.Id = 10;
            life.Add(li);
            int stateEvents = 0;
            DownloadState lastFrom = DownloadState.Removed, lastTo = DownloadState.Removed;
            life.StateChanged += delegate(DownloadItem changed, DownloadState from, DownloadState to)
            {
                stateEvents++;
                lastFrom = from;
                lastTo = to;
            };
            Check(life.Queue(li) && li.State == DownloadState.Queued, "lifecycle queue");
            Check(stateEvents == 1 && lastFrom == DownloadState.Paused && lastTo == DownloadState.Queued,
                  "lifecycle event");
            Check(!life.Queue(li) && stateEvents == 1, "lifecycle same state is a no-op");
            Check(life.Start(li) && li.State == DownloadState.Starting, "lifecycle start");
            Check(!life.SetState(li, DownloadState.Completed) && li.State == DownloadState.Starting,
                  "lifecycle rejects invalid transition");
            Check(life.SetState(li, DownloadState.Downloading) && life.Merge(li) && life.Complete(li) &&
                  li.State == DownloadState.Completed, "lifecycle download merge complete");
            Check(life.Pause(li) && li.State == DownloadState.Paused, "lifecycle edit pauses done row");
            Check(life.Queue(li) && life.Wait(li) && li.State == DownloadState.Waiting &&
                  life.Start(li), "lifecycle wait then start");
            Check(life.Fail(li) && li.State == DownloadState.Failed, "lifecycle fail");
            Check(life.Queue(li) && li.State == DownloadState.Queued, "lifecycle retry from failed");
            Check(life.Remove(li) && li.State == DownloadState.Removed && life.Count == 0 &&
                  lastTo == DownloadState.Removed, "lifecycle remove marks removed");

            DownloadManager nw = new DownloadManager();
            DownloadItem ni = new DownloadItem();
            nw.Add(ni);
            nw.Queue(ni); nw.Start(ni); nw.SetState(ni, DownloadState.Downloading);
            int waitEvents = 0;
            nw.StateChanged += delegate(DownloadItem changed, DownloadState from, DownloadState to)
            { waitEvents++; };
            Check(nw.WaitNetwork(ni) && ni.State == DownloadState.WaitingNetwork &&
                  waitEvents == 1, "lifecycle waiting network");
            Check(nw.Start(ni) && ni.State == DownloadState.Starting,
                  "lifecycle waiting network resumes");

            DownloadManager dmMove = new DownloadManager();
            DownloadItem m1 = new DownloadItem(); m1.Id = 1; m1.Priority = 0;
            DownloadItem m2 = new DownloadItem(); m2.Id = 2; m2.Priority = 0;
            DownloadItem m3 = new DownloadItem(); m3.Id = 3; m3.Priority = 1;
            List<DownloadItem> mItems = new List<DownloadItem>(); mItems.Add(m1); mItems.Add(m2); mItems.Add(m3);
            dmMove.AddRange(mItems);
            Check(dmMove.Move(m2, -1) && dmMove.Snapshot()[0] == m2 && dmMove.Snapshot()[1] == m1,
                  "manager move up");
            Check(!dmMove.Move(m2, -1), "manager move up at edge");
            Check(dmMove.Move(m2, 1) && dmMove.Snapshot()[0] == m1 && dmMove.Snapshot()[1] == m2,
                  "manager move down");
            Check(!dmMove.Move(m3, 1), "manager move skips other priority");

            DownloadManager dmDup = new DownloadManager();
            DownloadItem dupDone = new DownloadItem(); dupDone.Id = 1; dupDone.Status = "Done";
            dupDone.Url = "http://x:80/a"; dupDone.LocalPath = @"C:\dl\a.bin";
            DownloadItem dupQueued = new DownloadItem(); dupQueued.Id = 2; dupQueued.Status = "Queued";
            dupQueued.Url = "http://X/a/#frag"; dupQueued.LocalPath = @"C:\dl\b.bin";
            DownloadItem dupRemoved = new DownloadItem(); dupRemoved.Id = 3;
            dupRemoved.State = DownloadState.Removed; dupRemoved.Url = "http://y/z";
            dmDup.Add(dupDone); dmDup.Add(dupQueued); dmDup.Add(dupRemoved);
            Check(dmDup.FindByUrl("http://x/a/") == dupQueued, "duplicate url prefers unfinished");
            Check(dmDup.FindByPath(@"c:\DL\A.BIN") == dupDone, "duplicate path ignores case");
            Check(dmDup.FindByUrl("http://y/z") == null, "duplicate url skips removed");
            Check(dmDup.FindByUrl("http://nope/") == null, "duplicate url none");

            DownloadManager hist = new DownloadManager();
            DownloadItem h1 = new DownloadItem(); h1.Id = 1; h1.State = DownloadState.Completed;
            h1.Date = new DateTime(2026, 1, 1);
            DownloadItem h2 = new DownloadItem(); h2.Id = 2; h2.State = DownloadState.Failed;
            h2.Date = new DateTime(2026, 1, 2);
            DownloadItem h3 = new DownloadItem(); h3.Id = 3; h3.State = DownloadState.Completed;
            h3.Date = new DateTime(2026, 1, 3);
            DownloadItem h4 = new DownloadItem(); h4.Id = 4; h4.State = DownloadState.Downloading;
            hist.Add(h1); hist.Add(h2); hist.Add(h3); hist.Add(h4);
            Check(HistoryService.Trim(hist, 2, new DateTime(2026, 1, 4)) == 1 &&
                  hist.Count == 3 && hist.Find(1) == null && hist.Find(3) == h3 &&
                  hist.Find(4) == h4, "history trims oldest finished rows");

            DownloadManager histMerge = new DownloadManager();
            DownloadItem hp = new DownloadItem(); hp.Id = 10; hp.State = DownloadState.Completed;
            hp.Kind = "audio"; hp.MergeKey = "k"; hp.Date = new DateTime(2026, 1, 1);
            DownloadItem hc1 = new DownloadItem(); hc1.Id = 11; hc1.State = DownloadState.Completed;
            hc1.Date = new DateTime(2026, 1, 2);
            DownloadItem hc2 = new DownloadItem(); hc2.Id = 12; hc2.State = DownloadState.Completed;
            hc2.Date = new DateTime(2026, 1, 3);
            histMerge.Add(hp); histMerge.Add(hc1); histMerge.Add(hc2);
            Check(HistoryService.Trim(histMerge, 1, new DateTime(2026, 1, 4)) == 2 &&
                  histMerge.Count == 1 && histMerge.Find(10) == hp,
                  "history trim keeps pending merge audio");

            DownloadManager histOrphan = new DownloadManager();
            DateTime histNow = new DateTime(2026, 1, 4, 12, 0, 0);
            DownloadItem hOld = new DownloadItem(); hOld.Id = 20; hOld.Auto = true;
            hOld.Kind = "audio"; hOld.State = DownloadState.Waiting; hOld.MergeKey = "gone";
            hOld.Date = histNow.AddMinutes(-31);
            DownloadItem hYoung = new DownloadItem(); hYoung.Id = 21; hYoung.Auto = true;
            hYoung.Kind = "audio"; hYoung.State = DownloadState.Waiting; hYoung.MergeKey = "young";
            hYoung.Date = histNow.AddMinutes(-5);
            DownloadItem hPaired = new DownloadItem(); hPaired.Id = 22; hPaired.Auto = true;
            hPaired.Kind = "audio"; hPaired.State = DownloadState.Waiting; hPaired.MergeKey = "paired";
            hPaired.Date = histNow.AddMinutes(-31);
            DownloadItem hVideo = new DownloadItem(); hVideo.Id = 23;
            hVideo.Kind = "video"; hVideo.State = DownloadState.Completed; hVideo.MergeKey = "paired";
            histOrphan.Add(hOld); histOrphan.Add(hYoung); histOrphan.Add(hPaired); histOrphan.Add(hVideo);
            Check(HistoryService.Trim(histOrphan, 5000, histNow) == 1 &&
                  histOrphan.Find(20) == null && histOrphan.Find(21) == hYoung &&
                  histOrphan.Find(22) == hPaired, "history trim drops only old orphan audio");

            DownloadManager histClean = new DownloadManager();
            DownloadItem cAudio = new DownloadItem(); cAudio.Id = 30; cAudio.State = DownloadState.Completed;
            cAudio.Kind = "audio"; cAudio.MergeKey = "k";
            DownloadItem cDone = new DownloadItem(); cDone.Id = 31; cDone.State = DownloadState.Completed;
            DownloadItem cFail = new DownloadItem(); cFail.Id = 32; cFail.State = DownloadState.Failed;
            histClean.Add(cAudio); histClean.Add(cDone); histClean.Add(cFail);
            Check(HistoryService.Clean(histClean) == 2 && histClean.Count == 1 &&
                  histClean.Find(30) == cAudio, "history clean keeps pending merge audio");

            Check(HistoryService.NextPromptAt(120, true) == HistoryService.FirstPromptAt &&
                  HistoryService.NextPromptAt(120, false) == 120 + HistoryService.PromptStep,
                  "history prompt thresholds");

            string orDir = Path.Combine(Path.GetTempPath(), "opencode\\zid_orphan_test");
            try { Directory.Delete(orDir, true); } catch { }
            Directory.CreateDirectory(Path.Combine(orDir, "sub"));
            string ownedPath = Path.Combine(orDir, "owned.bin");
            File.WriteAllText(ownedPath, "keep");
            File.WriteAllText(ownedPath + ".aria2", "control");
            File.WriteAllText(ownedPath + ".part", "partial");
            File.WriteAllText(Path.Combine(orDir, "orphan.bin"), "partial");
            File.WriteAllText(Path.Combine(orDir, "orphan.bin.aria2"), "control");
            File.WriteAllText(Path.Combine(orDir, "loose.part"), "partial");
            File.WriteAllText(Path.Combine(orDir, "notes.txt"), "keep");
            File.WriteAllText(Path.Combine(Path.Combine(orDir, "sub"), "deep.bin"), "partial");
            File.WriteAllText(Path.Combine(Path.Combine(orDir, "sub"), "deep.bin.aria2"), "control");

            DownloadManager orphanDm = new DownloadManager();
            DownloadItem ownedRow = new DownloadItem();
            ownedRow.Id = 1; ownedRow.LocalPath = ownedPath;
            orphanDm.Add(ownedRow);
            int gone = HistoryService.RemoveOrphans(orphanDm, orDir);
            Check(gone == 5 &&
                  File.Exists(ownedPath) && File.Exists(ownedPath + ".aria2") &&
                  File.Exists(ownedPath + ".part") &&
                  !File.Exists(Path.Combine(orDir, "orphan.bin")) &&
                  !File.Exists(Path.Combine(orDir, "orphan.bin.aria2")) &&
                  !File.Exists(Path.Combine(orDir, "loose.part")) &&
                  !File.Exists(Path.Combine(Path.Combine(orDir, "sub"), "deep.bin")) &&
                  !File.Exists(Path.Combine(Path.Combine(orDir, "sub"), "deep.bin.aria2")) &&
                  File.Exists(Path.Combine(orDir, "notes.txt")),
                  "history clean removes only orphan partials");
            Check(HistoryService.RemoveOrphans(orphanDm, "") == 0 &&
                  HistoryService.RemoveOrphans(orphanDm, Path.Combine(orDir, "missing")) == 0,
                  "history orphan clean ignores a missing folder");
            try { Directory.Delete(orDir, true); } catch { }

            Check(SchedulePolicy.SecondsOfDay(new DateTime(2026, 1, 1, 13, 45, 30)) ==
                  13 * 3600 + 45 * 60 + 30, "schedule seconds of day");
            Check(SchedulePolicy.IsPending(36000, -1, false, 35999) &&
                  !SchedulePolicy.IsPending(36000, -1, false, 36000) &&
                  !SchedulePolicy.IsPending(-1, -1, false, 0), "schedule one-shot pending");
            Check(!SchedulePolicy.IsPending(36000, 43200, true, 39600) &&
                  SchedulePolicy.IsPending(36000, 43200, true, 35999) &&
                  SchedulePolicy.IsPending(36000, 43200, true, 43200),
                  "schedule daily window");
            Check(!SchedulePolicy.IsPending(79200, 21600, true, 82800) &&
                  !SchedulePolicy.IsPending(79200, 21600, true, 21599) &&
                  SchedulePolicy.IsPending(79200, 21600, true, 21600) &&
                  SchedulePolicy.IsPending(79200, 21600, true, 43200),
                  "schedule daily crosses midnight");
            Check(SchedulePolicy.IsPending(36000, -1, true, 35999) &&
                  !SchedulePolicy.IsPending(36000, -1, true, 36000),
                  "schedule daily without stop");
            Check(!SchedulePolicy.StopReached(36000, -1, true, 99999) &&
                  SchedulePolicy.StopReached(36000, 43200, false, 43200) &&
                  !SchedulePolicy.StopReached(36000, 43200, false, 43199),
                  "schedule stop one-shot");
            Check(!SchedulePolicy.StopReached(79200, 21600, true, 21599) &&
                  SchedulePolicy.StopReached(79200, 21600, true, 21600) &&
                  SchedulePolicy.StopReached(79200, 21600, true, 43200) &&
                  !SchedulePolicy.StopReached(79200, 21600, true, 79200),
                  "schedule stop crosses midnight");
            Check(SchedulePolicy.InWindow(32400, 82800, 32400) &&
                  SchedulePolicy.InWindow(32400, 82800, 82799) &&
                  !SchedulePolicy.InWindow(32400, 82800, 82800) &&
                  !SchedulePolicy.InWindow(32400, 82800, 32399),
                  "speed window inside hours");
            Check(SchedulePolicy.InWindow(82800, 21600, 82800) &&
                  SchedulePolicy.InWindow(82800, 21600, 3599) &&
                  !SchedulePolicy.InWindow(82800, 21600, 21600) &&
                  !SchedulePolicy.InWindow(82800, 21600, 43200),
                  "speed window crosses midnight");
            Check(SchedulePolicy.InWindow(36000, 36000, 0) &&
                  !SchedulePolicy.InWindow(-1, 43200, 36000),
                  "speed window equal bounds and unset");
            DateTime startNow = new DateTime(2026, 1, 1, 10, 0, 0);
            Check(SchedulePolicy.NextAt("09:00", startNow) == new DateTime(2026, 1, 2, 9, 0, 0) &&
                  SchedulePolicy.NextAt("14:00", startNow) == new DateTime(2026, 1, 1, 14, 0, 0) &&
                  SchedulePolicy.NextAt("", startNow) == DateTime.MinValue &&
                  SchedulePolicy.NextAt("bad", startNow) == DateTime.MinValue,
                  "per-item start time is the next occurrence");

            List<DownloadItem> media = new List<DownloadItem>();
            DownloadItem mVideo = new DownloadItem();
            mVideo.Id = 40; mVideo.Kind = "video"; mVideo.MergeKey = "k";
            mVideo.State = DownloadState.Completed;
            mVideo.LocalPath = @"C:\dl\Title - 1080p.mp4";
            DownloadItem mAudio = new DownloadItem();
            mAudio.Id = 41; mAudio.Kind = "audio"; mAudio.MergeKey = "k";
            mAudio.State = DownloadState.Completed;
            mAudio.LocalPath = @"C:\dl\Title - 129kbps.m4a";
            DownloadItem mOtherAudio = new DownloadItem();
            mOtherAudio.Id = 42; mOtherAudio.Kind = "audio"; mOtherAudio.MergeKey = "other";
            mOtherAudio.State = DownloadState.Completed;
            DownloadItem mMergedVideo = new DownloadItem();
            mMergedVideo.Id = 43; mMergedVideo.Kind = "video"; mMergedVideo.MergeKey = "k";
            mMergedVideo.State = DownloadState.Completed; mMergedVideo.Merged = true;
            media.Add(mVideo); media.Add(mAudio); media.Add(mOtherAudio); media.Add(mMergedVideo);
            Check(MediaService.FindPair(media, mVideo, "audio") == mAudio &&
                  MediaService.FindPair(media, mVideo, "video") == null &&
                  MediaService.FindPair(media, mAudio, "audio") == null,
                  "media finds one unmerged pair");

            DownloadItem pairVideo = null;
            DownloadItem pairAudio = null;
            Check(MediaService.FindMergePair(media, mAudio, out pairVideo, out pairAudio) ==
                  MergePairResult.Ready && pairVideo == mVideo && pairAudio == mAudio,
                  "media merge pair is ready");

            DownloadItem mSecondAudio = new DownloadItem();
            mSecondAudio.Id = 44; mSecondAudio.Kind = "audio"; mSecondAudio.MergeKey = "k";
            mSecondAudio.State = DownloadState.Completed;
            media.Add(mSecondAudio);
            Check(MediaService.FindMergePair(media, mVideo, out pairVideo, out pairAudio) ==
                  MergePairResult.AmbiguousAudio &&
                  MediaService.FindPair(media, mVideo, "audio") == null,
                  "media rejects two audio rows");
            media.Remove(mSecondAudio);

            DownloadItem mOutsider = new DownloadItem();
            mOutsider.Id = 45; mOutsider.Kind = "audio"; mOutsider.MergeKey = "k";
            mOutsider.State = DownloadState.Completed;
            Check(MediaService.FindMergePair(media, mOutsider, out pairVideo, out pairAudio) ==
                  MergePairResult.Missing, "media ignores a row outside the pair");
            mAudio.State = DownloadState.Downloading;
            Check(MediaService.FindMergePair(media, mVideo, out pairVideo, out pairAudio) ==
                  MergePairResult.Missing, "media waits for unfinished audio");
            mAudio.State = DownloadState.Completed;

            Check(MediaService.IsMp4(@"C:\dl\A.MP4") && MediaService.IsM4a(@"C:\dl\A.M4A") &&
                  !MediaService.IsMp4(@"C:\dl\A.webm") && !MediaService.IsM4a(null),
                  "media checks the merge extensions");

            string mediaDir = Path.Combine(Path.GetTempPath(), "zid_media_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mediaDir);
            string mediaExisting = Path.Combine(mediaDir, "Title.mp4");
            File.WriteAllText(mediaExisting, "x");
            string mediaVideo = Path.Combine(mediaDir, "Title - 1080p.mp4");
            Check(MediaService.MergeOutputPath(mediaVideo) == Path.Combine(mediaDir, "Title (1).mp4"),
                  "media merge output avoids an existing file");
            string mediaSame = Path.Combine(mediaDir, "Same.mp4");
            File.WriteAllText(mediaSame, "x");
            Check(MediaService.MergeOutputPath(mediaSame) == mediaSame,
                  "media merge output keeps the current name");
            try { Directory.Delete(mediaDir, true); } catch { }

            string muxDir = Path.Combine(Path.GetTempPath(), "zid_mux_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(muxDir);
            string muxVideo = Path.Combine(muxDir, "v.mp4");
            string muxAudio = Path.Combine(muxDir, "a.m4a");
            File.WriteAllText(muxVideo, "video");
            File.WriteAllText(muxAudio, "audio");
            string muxOut = Path.Combine(muxDir, "out.mp4");
            MergeFileResult muxOk = MediaService.MergeFiles(muxVideo, muxAudio, muxOut,
                new MuxFiles(MuxOk), null);
            Check(muxOk.Ok && !muxOk.SaveFailed && File.Exists(muxOut) &&
                  File.ReadAllText(muxOut) == "merged" && !File.Exists(muxVideo) &&
                  File.Exists(muxAudio) && !File.Exists(muxOut + ".tmp.mp4"),
                  "media merge installs the output and keeps audio");

            string muxSame = Path.Combine(muxDir, "same.mp4");
            File.WriteAllText(muxSame, "old");
            MergeFileResult muxReplace = MediaService.MergeFiles(muxSame, muxAudio, muxSame,
                new MuxFiles(MuxOk), null);
            Check(muxReplace.Ok && File.ReadAllText(muxSame) == "merged" &&
                  !File.Exists(muxSame + ".tmp.mp4"), "media merge replaces the video in place");

            MergeFileResult muxMissVideo = MediaService.MergeFiles(
                Path.Combine(muxDir, "missing.mp4"), muxAudio, muxOut, new MuxFiles(MuxOk), null);
            MergeFileResult muxMissAudio = MediaService.MergeFiles(
                muxSame, Path.Combine(muxDir, "missing.m4a"), muxOut, new MuxFiles(MuxOk), null);
            Check(!muxMissVideo.Ok && !muxMissVideo.SaveFailed &&
                  muxMissVideo.Error == "video file missing" &&
                  !muxMissAudio.Ok && muxMissAudio.Error == "audio file missing",
                  "media merge reports missing input");

            File.WriteAllText(muxVideo, "video");
            MergeFileResult muxBad = MediaService.MergeFiles(muxVideo, muxAudio, muxOut,
                new MuxFiles(MuxError), null);
            MergeFileResult muxEmpty = MediaService.MergeFiles(muxVideo, muxAudio, muxOut,
                new MuxFiles(MuxEmpty), null);
            Check(!muxBad.Ok && !muxBad.SaveFailed && muxBad.Error == "bad codec" &&
                  !muxEmpty.Ok && muxEmpty.Error == "muxer produced no output",
                  "media merge reports mux failures");

            string muxDirTarget = Path.Combine(muxDir, "target");
            Directory.CreateDirectory(muxDirTarget);
            MergeFileResult muxSave = MediaService.MergeFiles(muxVideo, muxAudio, muxDirTarget,
                new MuxFiles(MuxOk), null);
            Check(!muxSave.Ok && muxSave.SaveFailed && muxSave.Error.Length > 0 &&
                  !File.Exists(muxDirTarget + ".tmp.mp4"),
                  "media merge reports a failed install and cleans the temp file");
            try { Directory.Delete(muxDir, true); } catch { }


            QueueManager qm = new QueueManager();
            DownloadItem q1 = new DownloadItem(); q1.Id = 1; q1.Status = "Queued"; q1.Priority = 0;
            DownloadItem q2 = new DownloadItem(); q2.Id = 2; q2.Status = "Queued"; q2.Priority = 1;
            DownloadItem q3 = new DownloadItem(); q3.Id = 3; q3.Status = "Queued"; q3.Priority = 0;
            List<DownloadItem> qItems = new List<DownloadItem>();
            qItems.Add(q1); qItems.Add(q2); qItems.Add(q3);
            Check(qm.MaxConcurrent == 2 && qm.ActiveCount == 0, "queue defaults");
            Check(qm.HasQueued(qItems), "queue sees queued items");
            Check(!qm.TryStart(true) && qm.TryStart(false), "queue schedule gate");
            qm.Release();
            DownloadItem claimed;
            Check(qm.TryClaimNext(qItems, false, DateTime.Now, out claimed) && claimed == q2 &&
                  q2.State == DownloadState.Starting, "queue priority claim");
            Check(qm.TryClaimNext(qItems, false, DateTime.Now, out claimed) && claimed == q1, "queue next oldest");
            Check(!qm.TryClaimNext(qItems, false, DateTime.Now, out claimed) && qm.ActiveCount == 2,
                  "queue respects max concurrent");
            qm.Release();
            Check(qm.TryClaimNext(qItems, false, DateTime.Now, out claimed) && claimed == q3, "queue starts after release");
            Check(!qm.TryClaimNext(qItems, false, DateTime.Now, out claimed), "queue empty");

            QueueManager qd = new QueueManager();
            DownloadItem delayed = new DownloadItem(); delayed.Id = 4; delayed.Status = "Queued";
            delayed.StartAt = DateTime.Now.AddHours(1);
            DownloadItem ready = new DownloadItem(); ready.Id = 5; ready.Status = "Queued";
            List<DownloadItem> dItems = new List<DownloadItem>();
            dItems.Add(delayed); dItems.Add(ready);
            Check(qd.TryClaimNext(dItems, false, DateTime.Now, out claimed) && claimed == ready,
                  "queue skips a delayed row");
            qd.Release();
            Check(qd.TryClaimNext(dItems, false, delayed.StartAt.AddSeconds(1), out claimed) &&
                  claimed == delayed, "queue starts a delayed row when due");

            QueueManager qo = new QueueManager();
            DownloadItem o1 = new DownloadItem(); o1.Id = 9; o1.Status = "Queued"; o1.Priority = 0;
            DownloadItem o2 = new DownloadItem(); o2.Id = 2; o2.Status = "Queued"; o2.Priority = 0;
            List<DownloadItem> oItems = new List<DownloadItem>(); oItems.Add(o1); oItems.Add(o2);
            Check(qo.TryClaimNext(oItems, false, DateTime.Now, out claimed) && claimed == o1,
                  "queue uses list order not id");

            Check(EngineTaskState.Parse("active") == EngineTaskStatus.Active &&
                  EngineTaskState.Parse("waiting") == EngineTaskStatus.Waiting &&
                  EngineTaskState.Parse("paused") == EngineTaskStatus.Paused &&
                  EngineTaskState.Parse("complete") == EngineTaskStatus.Complete &&
                  EngineTaskState.Parse("error") == EngineTaskStatus.Error &&
                  EngineTaskState.Parse("removed") == EngineTaskStatus.Removed,
                  "engine maps aria2 status");
            Check(EngineTaskState.Parse(null) == EngineTaskStatus.Unknown &&
                  EngineTaskState.Parse("bogus") == EngineTaskStatus.Unknown,
                  "engine unknown status");

            FakeEngine fake = new FakeEngine();
            Check(fake.EnsureStarted() && fake.Started, "fake engine starts");
            DownloadItem fakeItem = new DownloadItem();
            fakeItem.LocalPath = "x.bin";
            Check(fake.Add(fakeItem, @"C:\dl", 4, 128, "proxy") == "gid-1" &&
                  fake.Calls.Contains(@"add:C:\dl:4:128:proxy"), "fake engine add");
            EngineTaskState running = new EngineTaskState();
            running.Status = EngineTaskStatus.Active;
            running.TotalLength = 100; running.CompletedLength = 25;
            running.DownloadSpeed = 10; running.Connections = 3;
            EngineTaskState finished = new EngineTaskState();
            finished.Status = EngineTaskStatus.Complete;
            finished.TotalLength = 100; finished.CompletedLength = 100;
            fake.Script.Add(running); fake.Script.Add(finished);
            EngineTaskState seen = fake.Tell("gid-1");
            Check(seen.Status == EngineTaskStatus.Active && seen.TotalLength == 100 &&
                  seen.CompletedLength == 25 && seen.DownloadSpeed == 10 && seen.Connections == 3,
                  "fake engine progress");
            Check(fake.Tell("gid-1").Status == EngineTaskStatus.Complete, "fake engine completion");
            EngineTaskState broken = new EngineTaskState();
            broken.Status = EngineTaskStatus.Error;
            broken.ErrorCode = 22; broken.ErrorMessage = "HTTP 403";
            fake.Script.Clear(); fake.Script.Add(broken);
            seen = fake.Tell("gid-1");
            Check(seen.Status == EngineTaskStatus.Error && seen.ErrorCode == 22 &&
                  seen.ErrorMessage == "HTTP 403", "fake engine failure");
            fake.ThrowOnTell = true;
            bool disconnected = false;
            try { fake.Tell("gid-1"); } catch { disconnected = true; }
            Check(disconnected, "fake engine disconnect");
            fake.ThrowOnTell = false;
            fake.Pause("gid-1"); fake.Unpause("gid-1"); fake.Remove("gid-1");
            fake.RemoveResult("gid-1"); fake.ChangeOption("gid-1", "k", "v"); fake.Stop();
            Check(fake.Recover() && fake.Calls.Contains("recover"), "fake engine recover");
            Check(fake.Calls.Contains("pause:gid-1") && fake.Calls.Contains("unpause:gid-1") &&
                  fake.Calls.Contains("remove:gid-1") && fake.Calls.Contains("remove-result:gid-1") &&
                  fake.Calls.Contains("option:gid-1:k=v") && fake.Calls.Contains("stop"),
                  "fake engine control calls");

            // T1.3b: startup reconciliation table (plan section 10).
            string rcDir = Path.Combine(Path.GetTempPath(), "zid_reconcile_test");
            try { Directory.Delete(rcDir, true); } catch { }
            Directory.CreateDirectory(rcDir);
            string rcDone = Path.Combine(rcDir, "done.bin");
            File.WriteAllBytes(rcDone, new byte[100]);

            DownloadItem rActive = new DownloadItem();
            rActive.Id = 1; rActive.Url = "http://host/a"; rActive.State = DownloadState.Downloading;
            rActive.LocalPath = Path.Combine(rcDir, "a.bin");
            DownloadItem rPaused = new DownloadItem();
            rPaused.Id = 2; rPaused.Url = "http://host/p"; rPaused.State = DownloadState.Paused;
            rPaused.LocalPath = Path.Combine(rcDir, "p.bin");
            DownloadItem rDone = new DownloadItem();
            rDone.Id = 3; rDone.Url = "http://host/d"; rDone.State = DownloadState.Downloading;
            rDone.LocalPath = rcDone; rDone.Size = 100;
            DownloadItem rError = new DownloadItem();
            rError.Id = 4; rError.Url = "http://host/e"; rError.State = DownloadState.Downloading;
            rError.LocalPath = Path.Combine(rcDir, "e.bin");
            DownloadItem rStale = new DownloadItem();
            rStale.Id = 5; rStale.Url = "http://host/s"; rStale.State = DownloadState.Waiting;
            rStale.LocalPath = Path.Combine(rcDir, "s.bin");
            DownloadItem rUrl = new DownloadItem();
            rUrl.Id = 6; rUrl.Url = "http://host/u"; rUrl.State = DownloadState.Queued;
            rUrl.LocalPath = Path.Combine(rcDir, "u.bin");
            List<DownloadItem> rItems = new List<DownloadItem>();
            rItems.Add(rActive); rItems.Add(rPaused); rItems.Add(rDone);
            rItems.Add(rError); rItems.Add(rStale); rItems.Add(rUrl);

            FakeEngine re = new FakeEngine();
            re.Gids.Add("g-active"); re.Gids.Add("g-paused"); re.Gids.Add("g-done");
            re.Gids.Add("g-error"); re.Gids.Add("g-stale"); re.Gids.Add("g-url");
            re.Gids.Add("g-orphan");

            EngineTaskState tActive = new EngineTaskState();
            tActive.Status = EngineTaskStatus.Active;
            tActive.TotalLength = 100; tActive.CompletedLength = 40;
            tActive.LocalPath = rActive.LocalPath;
            EngineTaskState tPaused = new EngineTaskState();
            tPaused.Status = EngineTaskStatus.Active;
            tPaused.LocalPath = rPaused.LocalPath;
            EngineTaskState tDone = new EngineTaskState();
            tDone.Status = EngineTaskStatus.Complete;
            tDone.TotalLength = 100; tDone.CompletedLength = 100;
            tDone.LocalPath = rcDone;
            EngineTaskState tError = new EngineTaskState();
            tError.Status = EngineTaskStatus.Error;
            tError.ErrorCode = 24; tError.ErrorText = "authorization failed (cookies?)";
            tError.LocalPath = rError.LocalPath;
            EngineTaskState tStale = new EngineTaskState();
            tStale.Status = EngineTaskStatus.Active;
            tStale.LocalPath = rStale.LocalPath;
            EngineTaskState tUrl = new EngineTaskState();
            tUrl.Status = EngineTaskStatus.Active;
            tUrl.Url = "http://host/u";
            EngineTaskState tOrphan = new EngineTaskState();
            tOrphan.Status = EngineTaskStatus.Active;
            tOrphan.LocalPath = Path.Combine(rcDir, "orphan.bin");
            re.Tasks["g-active"] = tActive; re.Tasks["g-paused"] = tPaused;
            re.Tasks["g-done"] = tDone; re.Tasks["g-error"] = tError;
            re.Tasks["g-stale"] = tStale; re.Tasks["g-url"] = tUrl;
            re.Tasks["g-orphan"] = tOrphan;

            List<string> recLog = new List<string>();
            ReconcileReport rr = Reconciler.Run(rItems, re,
                delegate(string m) { recLog.Add(m); });
            Check(rActive.EngineId == "g-active" && rActive.State == DownloadState.Downloading &&
                  rActive.Size == 100 && rActive.BytesReceived == 40 && rActive.Progress == 40,
                  "reconcile adopts an active task");
            Check(rPaused.EngineId == "g-paused" && rPaused.State == DownloadState.Paused &&
                  rPaused.Eta == "Paused" && re.Calls.Contains("pause:g-paused"),
                  "reconcile pauses a surviving task for a paused row");
            Check(rDone.State == DownloadState.Completed && rDone.Progress == 100 &&
                  rDone.BytesReceived == 100 && rDone.EngineId == null &&
                  re.Calls.Contains("remove-result:g-done"),
                  "reconcile verifies a finished engine task");
            Check(rError.State == DownloadState.Failed &&
                  rError.Eta.IndexOf("authorization") >= 0 &&
                  re.Calls.Contains("remove-result:g-error"),
                  "reconcile applies an engine error");
            Check(rStale.State == DownloadState.Waiting && re.Calls.Contains("remove:g-stale"),
                  "reconcile removes a stale task");
            Check(rUrl.EngineId == "g-url" && rUrl.State == DownloadState.Queued,
                  "reconcile matches a task by url");
            Check(recLog.Count > 0 && re.Calls.Contains("remove:g-orphan") &&
                  rr.Linked == 3 && rr.Completed == 1 && rr.Failed == 1 && rr.Orphans == 2,
                  "reconcile removes orphans and reports counts");

            string shortPath = Path.Combine(rcDir, "short.bin");
            File.WriteAllBytes(shortPath, new byte[10]);
            DownloadItem rShort = new DownloadItem();
            rShort.Id = 7; rShort.Url = "http://host/short";
            rShort.State = DownloadState.Downloading; rShort.LocalPath = shortPath;
            List<DownloadItem> shortItems = new List<DownloadItem>();
            shortItems.Add(rShort);
            FakeEngine reShort = new FakeEngine();
            reShort.Gids.Add("g-short");
            EngineTaskState tShort = new EngineTaskState();
            tShort.Status = EngineTaskStatus.Complete;
            tShort.TotalLength = 100; tShort.LocalPath = shortPath;
            reShort.Tasks["g-short"] = tShort;
            Reconciler.Run(shortItems, reShort, null);
            Check(rShort.State == DownloadState.Failed &&
                  rShort.Eta.IndexOf("incomplete") >= 0,
                  "reconcile rejects an incomplete completed file");

            FakeEngine reDown = new FakeEngine();
            reDown.Gids.Add("g1");
            reDown.ThrowOnList = true;
            int downLog = 0;
            ReconcileReport rDown = Reconciler.Run(rItems, reDown,
                delegate(string m) { downLog++; });
            Check(rDown.Changed == 0 && downLog > 0,
                  "reconcile tolerates a disconnected engine");
            try { Directory.Delete(rcDir, true); } catch { }

            int engineBuilt = 0;
            FakeEngine hostEngine = new FakeEngine();
            EngineHost host = new EngineHost(new EngineFactory(delegate ()
            {
                engineBuilt++;
                return hostEngine;
            }));
            Check(host.Current == null, "engine host starts empty");
            Check(host.Ensure() && host.Current == hostEngine && engineBuilt == 1 &&
                  hostEngine.Calls.Contains("ensure"), "engine host builds and starts once");
            Check(host.Ensure() && engineBuilt == 1 && hostEngine.Calls.Count == 2,
                  "engine host reuses the running engine");

            FakeEngine failingEngine = new FakeEngine();
            failingEngine.StartResult = false;
            EngineHost failingHost = new EngineHost(
                new EngineFactory(delegate { return failingEngine; }));
            Check(!failingHost.Ensure() && failingHost.LastError == "fake start failed" &&
                  failingHost.Current == failingEngine, "engine host records a start failure");
            Check(!failingHost.Ensure() && failingHost.LastError == "fake start failed" &&
                  failingEngine.Calls.Count == 2, "engine host retries the same engine");
            failingEngine.StartResult = true;
            Check(failingHost.Ensure() && failingHost.LastError == "fake start failed",
                  "engine host keeps the last error after recovery");

            // Local Range-capable server: full body, 206 slice and 416.
            byte[] rangeBody = new byte[128 * 1024];
            for (int i = 0; i < rangeBody.Length; i++) rangeBody[i] = (byte)(i * 31 + 7);
            using (RangeServer rs = new RangeServer(rangeBody))
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(rs.Url);
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    byte[] got = ReadAll(resp.GetResponseStream());
                    Check(resp.StatusCode == HttpStatusCode.OK && got.Length == rangeBody.Length &&
                          BytesEqual(got, 0, rangeBody, 0, got.Length),
                          "range server full body");
                }

                HttpWebRequest req2 = (HttpWebRequest)WebRequest.Create(rs.Url);
                req2.Proxy = null;
                req2.AddRange(100, 199);
                using (HttpWebResponse resp = (HttpWebResponse)req2.GetResponse())
                {
                    byte[] got = ReadAll(resp.GetResponseStream());
                    Check(resp.StatusCode == HttpStatusCode.PartialContent && got.Length == 100 &&
                          resp.Headers["Content-Range"] == "bytes 100-199/" + rangeBody.Length &&
                          BytesEqual(got, 0, rangeBody, 100, 100),
                          "range server partial body");
                }

                HttpWebRequest req3 = (HttpWebRequest)WebRequest.Create(rs.Url);
                req3.Proxy = null;
                req3.AddRange(rangeBody.Length + 10, rangeBody.Length + 20);
                bool unsatisfiable = false;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req3.GetResponse()) { }
                }
                catch (WebException we)
                {
                    HttpWebResponse resp = we.Response as HttpWebResponse;
                    unsatisfiable = resp != null && (int)resp.StatusCode == 416;
                    if (resp != null) resp.Close();
                }
                Check(unsatisfiable, "range server rejects an unsatisfiable range");

                Check(NetworkMonitor.Probe(rs.Url, 1000) &&
                      NetworkMonitor.CanProbe(rs.Url) &&
                      !NetworkMonitor.CanProbe("ftp://host/x") &&
                      !NetworkMonitor.Probe("nonsense", 100),
                      "network probe accepts a reachable server and rejects invalid urls");

                TcpListener closed = new TcpListener(IPAddress.Loopback, 0);
                closed.Start();
                int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
                closed.Stop();
                Check(!NetworkMonitor.Probe("http://127.0.0.1:" + closedPort + "/x", 500),
                      "network probe fails on a closed port");
            }

            // Critical Recovery Test, component level: kill aria2 mid-file,
            // keep partial + .aria2, restart and verify a resumed full file.
            string aria = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools"),
                                       "aria2c.exe");
            if (!File.Exists(aria))
            {
                Console.WriteLine("skip critical recovery (tools\\aria2c.exe missing)");
            }
            else
            {
                byte[] resumeBody = new byte[256 * 1024];
                for (int i = 0; i < resumeBody.Length; i++) resumeBody[i] = (byte)(i * 17 + 3);
                string crDir = Path.Combine(Path.GetTempPath(), "zid_critical_recovery");
                try { Directory.Delete(crDir, true); } catch { }
                Directory.CreateDirectory(crDir);
                string outPath = Path.Combine(crDir, "resume.bin");

                using (RangeServer rs = new RangeServer(resumeBody))
                {
                    rs.StallAfter = resumeBody.Length / 2;
                    Process first = StartAria(aria, crDir, "resume.bin", rs.Url);
                    DateTime limit = DateTime.Now.AddSeconds(20);
                    bool partial = false;
                    while (DateTime.Now < limit)
                    {
                        try
                        {
                            if (File.Exists(outPath) &&
                                new FileInfo(outPath).Length >= resumeBody.Length / 8)
                            {
                                partial = true;
                                break;
                            }
                        }
                        catch { }
                        Thread.Sleep(100);
                    }
                    Thread.Sleep(1500);      // auto-save-interval=1 writes .aria2
                    try { first.Kill(); } catch { }
                    try { first.WaitForExit(5000); } catch { }

                    long before = 0;
                    try { before = new FileInfo(outPath).Length; } catch { }
                    bool hasControl = File.Exists(outPath + ".aria2");
                    rs.ReleaseStall();
                    rs.StallAfter = -1;

                    Process second = StartAria(aria, crDir, "resume.bin", rs.Url);
                    bool exited = second.WaitForExit(60000);
                    int code = exited ? second.ExitCode : -1;
                    if (!exited) { try { second.Kill(); } catch { } }

                    byte[] final = File.Exists(outPath)
                        ? File.ReadAllBytes(outPath) : new byte[0];
                    string last = rs.LastRange;
                    Check(partial && hasControl,
                          "critical recovery keeps the partial file and .aria2 control file");
                    Check(exited && code == 0 && final.Length == resumeBody.Length &&
                          BytesEqual(final, 0, resumeBody, 0, final.Length),
                          "critical recovery finishes with a verified file");
                    Check(RangeStart(last) > 0 && RangeStart(last) >= before / 2,
                          "critical recovery resumes from the partial offset");
                }
                try { Directory.Delete(crDir, true); } catch { }
            }

            string cfgPath = Path.Combine(Path.GetTempPath(), "zid_config_test.txt");
            try { File.Delete(cfgPath); File.Delete(cfgPath + ".bak"); File.Delete(cfgPath + ".tmp"); } catch { }
            AppSettings cfgDefault = SettingsStore.Load(cfgPath);
            Check(cfgDefault.Theme == "light" && cfgDefault.MaxConcurrent == 2 &&
                  cfgDefault.PartsPerDownload == 16 && cfgDefault.Language == "en" &&
                  cfgDefault.ScheduleSecs == -1 && cfgDefault.StopSecs == -1 &&
                  cfgDefault.UpdateCheck && cfgDefault.UpdateDays == 7 &&
                  cfgDefault.LastUpdateCheck == 0,
                  "settings defaults when file is missing");

            AppSettings cfgOut = new AppSettings();
            cfgOut.Theme = "dark";
            cfgOut.MaxConcurrent = 5;
            cfgOut.PartsPerDownload = 8;
            cfgOut.MaxSpeedKbps = 4096;
            SettingsStore.RememberCredential(cfgOut, "https://Example.com/a.zip", "alice", "s3cret");
            cfgOut.SpeedLimitKbps = 200;
            cfgOut.SpeedLimitFrom = "09:00";
            cfgOut.SpeedLimitTo = "23:00";
            cfgOut.Proxy = "http://user:pass@127.0.0.1:8080";
            cfgOut.CaptureExt = "zip,7z";
            cfgOut.BlockedHosts = "example.com, foo.com";
            cfgOut.NeatToken = "token123";
            cfgOut.ClipboardWatch = true;
            cfgOut.ShutdownWhenDone = true;
            cfgOut.AutoSort = true;
            cfgOut.NamingTemplate = "{host}/{type}/{name}";
            cfgOut.ScheduleAt = "09:30";
            cfgOut.StopAt = "18:00";
            cfgOut.ScheduleDaily = true;
            cfgOut.Language = "de";
            cfgOut.UpdateCheck = false;
            cfgOut.UpdateDays = 14;
            cfgOut.LastUpdateCheck = 638000000000000000L;
            cfgOut.Prefs = "sort=name&p1=%23ff0000";
            cfgOut.WindowWidth = 1000; cfgOut.WindowHeight = 700;
            cfgOut.WindowX = 12; cfgOut.WindowY = 34; cfgOut.WindowMaximized = true;
            SettingsStore.Save(cfgPath, cfgOut);
            AppSettings cfgIn = SettingsStore.Load(cfgPath);
            Check(SettingsStore.SavedCredential(cfgIn, "https://example.com/b") == "alice\ns3cret",
                  "settings round trip saves a site credential");
            string cfgCredText = File.ReadAllText(cfgPath);
            Check(cfgCredText.IndexOf("s3cret") < 0 &&
                  cfgCredText.IndexOf("credentials=") >= 0,
                  "settings never write a plaintext password");
            SettingsStore.ForgetCredential(cfgIn, "https://example.com/b");
            Check(SettingsStore.SavedCredential(cfgIn, "https://example.com/b") == null,
                  "settings forget one site credential");
            SettingsStore.RememberCredential(cfgIn, "https://example.com/b", "bob", "pw");
            SettingsStore.ForgetAllCredentials(cfgIn);
            Check(cfgIn.Credentials.Count == 0, "settings forget all site credentials");
            Check(cfgIn.MaxConcurrent == 5 && cfgIn.PartsPerDownload == 8 &&
                  cfgIn.MaxSpeedKbps == 4096 && cfgIn.Proxy == cfgOut.Proxy &&
                  cfgIn.SpeedLimitKbps == 200 &&
                  cfgIn.SpeedLimitFromSecs == 9 * 3600 &&
                  cfgIn.SpeedLimitToSecs == 23 * 3600 &&
                  cfgIn.CaptureExt == "zip,7z" && cfgIn.NeatToken == "token123" &&
                  cfgIn.BlockedHosts == "example.com, foo.com" &&
                  cfgIn.ClipboardWatch && cfgIn.ShutdownWhenDone && cfgIn.AutoSort &&
                  cfgIn.NamingTemplate == "{host}/{type}/{name}" &&
                  cfgIn.ScheduleSecs == 9 * 3600 + 30 * 60 &&
                  cfgIn.StopSecs == 18 * 3600 && cfgIn.ScheduleDaily &&
                  cfgIn.Language == "de" && cfgIn.Prefs == cfgOut.Prefs &&
                  !cfgIn.UpdateCheck && cfgIn.UpdateDays == 14 &&
                  cfgIn.LastUpdateCheck == 638000000000000000L &&
                  cfgIn.WindowWidth == 1000 && cfgIn.WindowHeight == 700 &&
                  cfgIn.WindowX == 12 && cfgIn.WindowY == 34 && cfgIn.WindowMaximized,
                  "settings round trip");
            Check(cfgIn.Theme == "light", "settings theme stays light");

            File.WriteAllText(cfgPath,
                "theme=dark\r\n" +
                "maxConcurrent=99\r\n" +
                "partsPerDownload=0\r\n" +
                "maxSpeedKbps=-5\r\n" +
                "speedLimitKbps=-1\r\n" +
                "speedLimitFrom=25:99\r\n" +
                "speedLimitTo=bad\r\n" +
                "scheduleAt=25:99\r\n" +
                "stopAt=bad\r\n" +
                "window=800,600,10,20,1\r\n" +
                "language=fr\r\n" +
                "prefs=a%3Db\r\n");
            AppSettings cfgBad = SettingsStore.Load(cfgPath);
            Check(cfgBad.Theme == "light" && cfgBad.MaxConcurrent == 2 &&
                  cfgBad.PartsPerDownload == 16 && cfgBad.MaxSpeedKbps == 0 &&
                  cfgBad.SpeedLimitKbps == 0 && cfgBad.SpeedLimitFrom == "" &&
                  cfgBad.SpeedLimitTo == "" &&
                  cfgBad.ScheduleAt == "" && cfgBad.ScheduleSecs == -1 &&
                  cfgBad.StopAt == "" && cfgBad.StopSecs == -1 &&
                  cfgBad.WindowWidth == 800 && cfgBad.WindowHeight == 600 &&
                  cfgBad.WindowX == 10 && cfgBad.WindowY == 20 && cfgBad.WindowMaximized &&
                  cfgBad.Language == "fr" && cfgBad.Prefs == "a%3Db",
                  "settings clamp and ignore bad values");

            File.WriteAllText(cfgPath, "maxConcurrent=8\r\nmaxSpeedKbps=0\r\n");
            AppSettings cfgMax = SettingsStore.Load(cfgPath);
            Check(cfgMax.MaxConcurrent == 8 && cfgMax.MaxSpeedKbps == 0,
                  "settings accepts limits at the edge");

            SettingsStore.Save(cfgPath, new AppSettings());
            string cfgText = File.ReadAllText(cfgPath);
            Check(cfgText.IndexOf("window=") < 0 &&
                  cfgText.IndexOf("theme=light") >= 0 &&
                  cfgText.IndexOf("language=en") >= 0,
                  "settings omit an empty window");
            try { File.Delete(cfgPath); } catch { }

            Check(UpdateChecker.VersionUrl != UpdateChecker.VersionUrlFallback &&
                  UpdateChecker.VersionUrlFallback.StartsWith("https://") &&
                  UpdateChecker.VersionUrlFallback.IndexOf("github.com") < 0,
                  "update check has a fallback on another host");
            Check(UpdateChecker.CompareVersions("1.0", "1.0.0.0") == 0,
                  "update version pads missing parts");
            Check(UpdateChecker.CompareVersions("1.1", "1.0") > 0 &&
                  UpdateChecker.CompareVersions("1.0", "1.1") < 0,
                  "update version ordering");
            Check(UpdateChecker.CompareVersions("2.0-beta", "1.9") > 0 &&
                  UpdateChecker.CompareVersions("1.10", "1.9") > 0,
                  "update version ignores suffix and compares numerically");

            UpdateChecker.UpdateInfo upd = UpdateChecker.Parse(
                "{\"version\":\"1.2\",\"setup\":\"setup_1.2.exe\",\"whats_new\":[\"x\"]}");
            Check(upd != null && upd.Version == "1.2" && upd.Setup == "setup_1.2.exe" &&
                  upd.Notes == "- x" &&
                  UpdateChecker.IsNewer("1.2", "1.0.0.0") &&
                  !UpdateChecker.IsNewer("1.0", "1.0.0.0"),
                  "update json parse and compare");
            UpdateChecker.UpdateInfo noted = UpdateChecker.Parse(
                "{\"version\":\"1.3\",\"notes\":\"Fix A\\nFix B\"}");
            Check(noted != null && noted.Notes == "Fix A\nFix B",
                  "update json reads release notes");
            Check(UpdateChecker.Parse("") == null &&
                  UpdateChecker.Parse("{\"setup\":\"a.exe\"}") == null,
                  "update json without a version is rejected");
            Check(UpdateChecker.SafeSetupName("..\\..\\evil.exe", "1.2") == "setup_1.2.exe" &&
                  UpdateChecker.SafeSetupName("setup.exe", "1.2") == "setup.exe" &&
                  UpdateChecker.SafeSetupName("", "1.2") == "setup_1.2.exe" &&
                  UpdateChecker.SafeSetupName("a b.exe", "1.2") == "setup_1.2.exe",
                  "update setup name is sanitized");

            string logPath = Path.Combine(Path.GetTempPath(), "zid_logger_test.log");
            try { File.Delete(logPath); File.Delete(logPath + ".1"); } catch { }
            Logger.Init(logPath);
            Logger.Info("hello logger");
            Check(File.Exists(logPath) && Logger.Tail(10).IndexOf("hello logger") >= 0,
                  "logger writes and tails");
            Logger.Info("Cookie: a=1; b=2");
            Check(Logger.Tail(5).IndexOf("a=1") < 0 && Logger.Tail(5).IndexOf("<masked>") >= 0,
                  "logger masks cookie header");
            Logger.Warn("Authorization: Bearer xyz");
            Check(Logger.Tail(5).IndexOf("xyz") < 0 &&
                  Logger.Tail(5).IndexOf("Authorization: <masked>") >= 0,
                  "logger masks authorization");
            Check(Logger.Mask("https://user:pass@example.com/x?token=abc&b=1") ==
                  "https://user:<masked>@example.com/x?token=<masked>&b=1",
                  "logger masks url credentials");
            Check(Logger.Mask(null) == "", "logger mask null");
            Logger.Error("recent error one");
            Logger.Info("not an error");
            Logger.Error("recent error two");
            string recent = Logger.RecentErrors(1);
            Check(recent.IndexOf("recent error two") >= 0 &&
                  recent.IndexOf("recent error one") < 0 &&
                  recent.IndexOf("not an error") < 0, "logger recent errors only");
            try { File.Delete(logPath); File.Delete(logPath + ".1"); } catch { }

            Console.WriteLine(_fail == 0 ? "ALL TESTS PASSED" : _fail + " TEST(S) FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        private static bool MuxOk(string videoPath, string audioPath, string outPath, out string error)
        {
            error = "";
            File.WriteAllText(outPath, "merged");
            return true;
        }

        private static bool MuxError(string videoPath, string audioPath, string outPath, out string error)
        {
            error = "bad codec";
            return false;
        }

        private static bool MuxEmpty(string videoPath, string audioPath, string outPath, out string error)
        {
            error = "";
            return true;
        }

        private static Process StartAria(string exe, string dir, string name, string url)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments =
                "--continue=true --split=1 --max-connection-per-server=1 --file-allocation=none" +
                " --auto-file-renaming=false --allow-overwrite=true --quiet=true" +
                " --console-log-level=error --auto-save-interval=1 --max-tries=1" +
                " --connect-timeout=5 --timeout=10 --dir=\"" + dir + "\" --out=" + name +
                " \"" + url + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate { };
            p.ErrorDataReceived += delegate { };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        // Minimal loopback HTTP/1.1 server with byte ranges. Tests only: it is
        // deliberately in this file so the shipping exe does not carry it.
        private sealed class RangeServer : IDisposable
        {
            private readonly byte[] _body;
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private readonly ManualResetEvent _stall = new ManualResetEvent(false);
            private readonly object _sync = new object();
            private readonly List<string> _ranges = new List<string>();
            private volatile bool _stop;
            internal volatile int StallAfter = -1;

            internal RangeServer(byte[] body)
            {
                _body = body;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            internal int Port { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }
            internal string Url { get { return "http://127.0.0.1:" + Port + "/file.bin"; } }

            internal string LastRange
            {
                get { lock (_sync) return _ranges.Count == 0 ? "" : _ranges[_ranges.Count - 1]; }
            }

            internal void ReleaseStall() { _stall.Set(); }

            public void Dispose()
            {
                _stop = true;
                try { _listener.Stop(); } catch { }
                _stall.Set();
            }

            private void Loop()
            {
                while (!_stop)
                {
                    TcpClient c;
                    try { c = _listener.AcceptTcpClient(); }
                    catch { return; }
                    ThreadPool.QueueUserWorkItem(delegate(object o) { Serve((TcpClient)o); }, c);
                }
            }

            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        NetworkStream s = client.GetStream();
                        string head = ReadHeaders(s);
                        if (head.Length == 0) return;

                        int start = 0, end = _body.Length - 1;
                        bool partial = false;
                        string[] lines = head.Replace("\r\n", "\n").Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (!lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string spec = lines[i].Substring(6).Trim();
                            if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                            spec = spec.Substring(6).Trim();
                            int dash = spec.IndexOf('-');
                            if (dash < 0) continue;
                            string a = spec.Substring(0, dash);
                            string b = spec.Substring(dash + 1);
                            int n;
                            if (a.Length == 0)
                            {
                                if (int.TryParse(b, out n) && n > 0)
                                    start = Math.Max(0, _body.Length - n);
                            }
                            else if (int.TryParse(a, out n)) start = n;
                            if (b.Length > 0 && int.TryParse(b, out n) && n < end) end = n;
                            if (end > _body.Length - 1) end = _body.Length - 1;
                            partial = true;
                        }

                        lock (_sync)
                            _ranges.Add(partial ? start + "-" + end : "full");

                        if (start < 0 || start >= _body.Length || start > end)
                        {
                            byte[] bad = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                                "Content-Range: bytes */" + _body.Length + "\r\n" +
                                "Content-Length: 0\r\nConnection: close\r\n\r\n");
                            s.Write(bad, 0, bad.Length);
                            s.Flush();
                            return;
                        }

                        int count = end - start + 1;
                        StringBuilder h = new StringBuilder();
                        h.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                        h.Append("Content-Type: application/octet-stream\r\n");
                        h.Append("Accept-Ranges: bytes\r\n");
                        h.Append("Content-Length: ").Append(count).Append("\r\n");
                        if (partial)
                            h.Append("Content-Range: bytes ").Append(start).Append('-')
                             .Append(end).Append('/').Append(_body.Length).Append("\r\n");
                        h.Append("Connection: close\r\n\r\n");
                        byte[] header = Encoding.ASCII.GetBytes(h.ToString());
                        s.Write(header, 0, header.Length);

                        int sent = 0;
                        int stall = StallAfter;
                        while (sent < count)
                        {
                            if (stall >= 0 && sent >= stall)
                            {
                                // Hold the rest back so the client can be killed
                                // mid-download with a real partial file.
                                _stall.WaitOne(15000);
                                return;
                            }
                            int n = count - sent;
                            if (stall >= 0 && sent + n > stall) n = stall - sent;
                            s.Write(_body, start + sent, n);
                            s.Flush();
                            sent += n;
                        }
                    }
                    catch { }
                }
            }

            private static string ReadHeaders(Stream s)
            {
                StringBuilder sb = new StringBuilder();
                byte[] one = new byte[1];
                while (sb.Length < 8192)
                {
                    int n = s.Read(one, 0, 1);
                    if (n <= 0) break;
                    sb.Append((char)one[0]);
                    int len = sb.Length;
                    if (len >= 4 && sb[len - 4] == '\r' && sb[len - 3] == '\n' &&
                        sb[len - 2] == '\r' && sb[len - 1] == '\n') break;
                }
                return sb.ToString();
            }
        }

        // Test double: scripted task states, recorded calls, optional disconnect.
        private sealed class FakeNetworkHost : INetworkHost
        {
            internal readonly List<DownloadItem> Resumed = new List<DownloadItem>();
            internal bool Available = true;
            internal bool ProbeResult = true;
            internal int ProbeCount;
            internal int PushCount;
            internal int LogCount;

            public List<DownloadItem> Snapshot() { return new List<DownloadItem>(); }
            public void ResumeAfterWait(DownloadItem item) { Resumed.Add(item); }
            public int StaggerDelay(int index) { return 0; }
            public bool NetworkAvailable() { return Available; }
            public bool ProbeConnection(string url, int timeoutMs)
            {
                ProbeCount++;
                return ProbeResult;
            }
            public void PushState() { PushCount++; }
            public void Log(string message) { LogCount++; }
        }

        private sealed class FakeWorkerHost : IWorkerHost
        {
            internal readonly DownloadManager Manager = new DownloadManager();
            internal readonly QueueManager Queues = new QueueManager();
            internal readonly FakeEngine Fake = new FakeEngine();
            internal bool EngineResult = true;
            internal string EngineErrorText = "";
            internal string AriaExePath = "";
            internal long FreeBytes = long.MaxValue;
            internal int PushCount;
            internal int FlushCount;
            internal int StartNextCount;
            internal int AskCount;
            internal bool AskResult;

            public DownloadManager Downloads { get { return Manager; } }
            public QueueManager Queue { get { return Queues; } }
            public string AriaExe { get { return AriaExePath; } }
            public string ToolsDir { get { return ""; } }
            public string DownloadFolder { get { return ""; } }
            public int PartsPerDownload { get { return 4; } }
            public long MaxSpeedKbps { get { return 0; } }
            public string Proxy { get { return ""; } }
            public IDownloadEngine Engine { get { return Fake; } }
            public string EngineError { get { return EngineErrorText; } }
            public bool EnsureEngine() { return EngineResult; }
            public long FreeSpaceFor(string path) { return FreeBytes; }
            public void ForgetGid(DownloadItem item) { item.EngineId = null; }
            public bool CheckVideoAudio(DownloadItem video) { return false; }
            public bool AskCredentials(DownloadItem item)
            {
                AskCount++;
                if (!AskResult) return false;
                item.AuthUser = "u";
                item.AuthPass = "p";
                return true;
            }
            public void TryMerge(DownloadItem item) { }
            public void SaveState() { }
            public void SaveStateAndFlush() { FlushCount++; }
            public void PushState() { PushCount++; }
            public void TryStartNext() { StartNextCount++; }
            public void Log(string message) { }
            public void ReportCrash(string context, Exception ex) { }
        }

        private sealed class FakeEngine : IDownloadEngine
        {
            internal readonly List<EngineTaskState> Script = new List<EngineTaskState>();
            internal readonly Dictionary<string, EngineTaskState> Tasks =
                new Dictionary<string, EngineTaskState>();
            internal readonly List<string> Gids = new List<string>();
            internal readonly List<string> Calls = new List<string>();
            internal bool Started;
            internal bool StartResult = true;
            internal bool RecoverResult = true;
            internal bool ThrowOnTell;
            internal bool ThrowOnList;
            internal string Gid = "gid-1";
            private int _tell;

            public bool EnsureStarted() { Started = true; Calls.Add("ensure"); return StartResult; }
            public bool Recover() { Calls.Add("recover"); return RecoverResult; }
            public string LastError { get { return StartResult ? "" : "fake start failed"; } }
            public void Stop() { Calls.Add("stop"); }

            public List<string> ListGids()
            {
                Calls.Add("list");
                if (ThrowOnList) throw new Exception("disconnected");
                return new List<string>(Gids);
            }

            public string Add(DownloadItem item, string defaultFolder, int defaultParts,
                              long defaultSpeed, string defaultProxy)
            {
                Calls.Add("add:" + defaultFolder + ":" + defaultParts + ":" + defaultSpeed +
                          ":" + defaultProxy);
                return Gid;
            }

            public EngineTaskState Tell(string gid)
            {
                Calls.Add("tell:" + gid);
                if (ThrowOnTell) throw new Exception("disconnected");
                EngineTaskState task;
                if (Tasks.TryGetValue(gid, out task)) return task;
                if (Script.Count == 0) return new EngineTaskState();
                EngineTaskState state = Script[Math.Min(_tell, Script.Count - 1)];
                _tell++;
                return state;
            }

            public void Pause(string gid) { Calls.Add("pause:" + gid); }
            public void Unpause(string gid) { Calls.Add("unpause:" + gid); }
            public void Remove(string gid) { Calls.Add("remove:" + gid); }
            public void RemoveResult(string gid) { Calls.Add("remove-result:" + gid); }
            public void ChangeOption(string gid, string name, string value)
            {
                Calls.Add("option:" + gid + ":" + name + "=" + value);
            }
        }
    }
}
