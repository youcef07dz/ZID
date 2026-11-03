using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZID
{
    // =====================================================================
    //  Main window
    // =====================================================================
    public class MainForm : Form, IWorkerHost, INetworkHost
    {
        private readonly WebBrowser webBrowser1;
        private readonly DownloadManager _downloads = new DownloadManager();
        private readonly QueueManager _queue = new QueueManager();
        // Keep history bounded; active and pending-merge items are never pruned.
        private int _historyWarnAt = HistoryService.FirstPromptAt;
        private bool _historyPrompting;
        private DateTime _filesCheckedAt = DateTime.MinValue;
        private readonly object _lock     = new object();
        private readonly object _speedLock = new object();
        private readonly System.Windows.Forms.Timer _ticker = new System.Windows.Forms.Timer();
        private int _lastPushTick;
        private int _lastTaskbarPct = -1;
        private int _lastTaskbarState = -1;
        private int _langVer;
        private readonly EngineHost _engines;
        private readonly DownloadWorker _worker;
        private readonly NetworkRecovery _net;
        private readonly CapturePolicy _capture = new CapturePolicy();
        private IDownloadEngine _engine { get { return _engines.Current; } }
        private string _engineError { get { return _engines.LastError; } }
        private bool _reconciled;
        private readonly object _reconcileLock = new object();
        private bool _updateChecking;
        private readonly Dictionary<int, DownloadProgressForm> _popups =
            new Dictionary<int, DownloadProgressForm>();
        private bool _documentReady;
        private readonly string _downloadFolder;
        private bool _sleepBlocked;
        private string _lastPushed;
        private ClipWatcher _clipWatcher;
        private bool _shutdownStarted;

        private NotifyIcon _tray;
        private bool _reallyExit;
        private bool _uiMaximized;
        private AppSettings _settings = new AppSettings();

        private const int WM_NCHITTEST = 0x84;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const int ResizeBorder = 5;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        // OLECMDID_OPTICAL_ZOOM via CGID_MSHTML: reflows the page like Ctrl+Plus.
        [ComImport, Guid("b722bccb-4e68-101b-a2bc-00aa00404770"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleCommandTarget
        {
            [PreserveSig]
            int QueryStatus(ref Guid pguidCmdGroup, int cCmds, IntPtr prgCmds, IntPtr pCmdText);
            [PreserveSig]
            int Exec(ref Guid pguidCmdGroup, int nCmdID, int nCmdeOpt,
                     [MarshalAs(UnmanagedType.Struct)] ref object pvaIn, IntPtr pvaOut);
        }

        private readonly string _stateFile;
        private readonly StateStore _stateStore;
        private readonly StateWriter _stateWriter;
        private readonly string _configFile;
        private readonly string _toolsDir;
        private readonly string _langDir;
        private readonly string _ariaExe;
        private readonly string _ariaCerts;

        private int _maxConcurrent
        {
            get { return _queue.MaxConcurrent; }
            set { _queue.MaxConcurrent = value; }
        }
        // Common download types, used until the user edits the list in Settings.
        private const string DefaultCaptureExt =
            "7z,zip,rar,tar,gz,tgz,bz2,xz,iso,img," +
            "exe,msi,msix,appx,apk,dmg,deb,rpm,pkg,jar," +
            "pdf,doc,docx,xls,xlsx,ppt,pptx,odt,ods,odp,rtf,txt,csv,epub,mobi,azw3,djvu," +
            "mp4,mkv,avi,mov,wmv,flv,webm,m4v,mpg,mpeg,ts,m2ts,3gp,ogv," +
            "mp3,flac,wav,aac,m4a,ogg,oga,opus,wma,mid,midi,aiff,ape," +
            "jpg,jpeg,png,gif,bmp,webp,tif,tiff,svg,heic,ico,psd," +
            "ttf,otf,woff,woff2," +
            "json,xml,sql,db,sqlite,torrent,vhd,vhdx,vmdk";
        private bool _stopFired;
        // True while the temporary speed cap is applied to active downloads.
        private bool _speedWindowApplied;
        private string _langDirName = "ltr";
        private Dictionary<string, string> _lang = new Dictionary<string, string>();
        private string _languagesJson = "[]";
        private NeatLinkServer _neatLink;

        public MainForm()
        {
            webBrowser1 = new WebBrowser();
            webBrowser1.Dock = DockStyle.Fill;
            webBrowser1.IsWebBrowserContextMenuEnabled = false;
            webBrowser1.WebBrowserShortcutsEnabled = true;
            webBrowser1.ScriptErrorsSuppressed = true;
            try
            {
                if (webBrowser1.Version.Major < 8)
                {
                    MessageBox.Show("This app needs Internet Explorer 8 or newer.",
                        "ZyZoom Internet Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                }
            }
            catch { }

            // The title bar lives in ui.html so it follows the app theme;
            // the native one is switched off.
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(4);
            AutoScaleMode = AutoScaleMode.None;

            ClientSize  = new Size(Theme.Px(1050), Theme.Px(620));
            Text        = "ZyZoom Internet Downloader";
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }
            MinimumSize = new Size(Theme.Px(720), Theme.Px(360));
            Controls.Add(webBrowser1);

            string dataDir = AppPaths.DataDir;
            Directory.CreateDirectory(dataDir);
            _stateFile  = Path.Combine(dataDir, "state.json");
            _stateStore = new StateStore(_stateFile);
            _stateWriter = new StateWriter(new StateWrite(_stateStore.SaveCaptured), new StateLog(Log));
            _configFile = Path.Combine(dataDir, "config.txt");
            _toolsDir   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            _langDir    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");
            _ariaExe    = Path.Combine(_toolsDir, "aria2c.exe");
            _ariaCerts  = CaBundle.Prepare(_toolsDir, dataDir);
            _engines    = new EngineHost(_ariaExe, _toolsDir, _ariaCerts);
            _worker     = new DownloadWorker(this);
            _net        = new NetworkRecovery(this);
            RegisterProtocol();

            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(profile))
                profile = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            _downloadFolder = Path.Combine(Path.Combine(profile, "Downloads"), "ZID");
            if (AppPaths.Portable)
                _downloadFolder = Path.Combine(AppPaths.DataDir, "downloads");
            Directory.CreateDirectory(_downloadFolder);

            LoadConfig();
            _settings.CaptureExt = NormalizeCaptureExt(_settings.CaptureExt);
            // Older installs saved an empty list: treat that as "use the defaults".
            // shortcut: an intentionally cleared list returns to defaults on restart;
            // upgrade to a sentinel value if "capture nothing" must survive a restart.
            if (_settings.CaptureExt.Length == 0) _settings.CaptureExt = NormalizeCaptureExt(DefaultCaptureExt);
            if (_settings.NeatToken.Length == 0) _settings.NeatToken = Guid.NewGuid().ToString("N");
            WriteCaptureConfig();
            ApplyProgressColors();
            ApplySavedWindow();
            BackColor = Theme.Frame(_settings.Theme == "dark");
            LoadLanguage();
            BuildTray();
            _downloads.StateChanged += OnDownloadStateChanged;
            LoadState();

            // Browser extension channel: ws://127.0.0.1:10007/download.
            _neatLink = new NeatLinkServer();
            _neatLink.Token = _settings.NeatToken;
            _neatLink.DownloadRequested += OnNeatDownload;
            if (!_neatLink.Start()) Log("neatlink: port " + NeatLinkServer.Port + " is unavailable" +
                                        (Util.IsBlank(_neatLink.LastError) ? "" : ": " + _neatLink.LastError));

            // Unfinished downloads from a crash/kill continue automatically.
            foreach (DownloadItem it in _downloads.Snapshot())
            {
                if (!it.ResumeOnLoad) continue;
                it.ResumeOnLoad = false;
                ResumeItem(it, false);
            }

            // Optional: "ZID.exe <url>" adds a download on startup.
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    BackendAdd(a, "", "", "", "", "");
            }

            _ticker.Interval = 1000;
            _ticker.Tick += delegate
            {
                CheckTitleChannel(); CheckSchedule(); CheckHistoryPrompt(); CheckAfterDone();
                CheckRefreshWaits(); _net.Check(false);
                CheckAutoUpdate();
                int active = UpdateTaskbar();
                // Idle: no JSON build / COM marshal, just a slow heartbeat.
                if (active > 0 || unchecked(Environment.TickCount - _lastPushTick) >= 5000)
                    PushState();
                MaybeShutdown();
            };
            _ticker.Start();

            Shown += delegate
            {
                ApplyClipWatch();
                MergeSweepAsync();
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
                NetworkChange.NetworkAvailabilityChanged += delegate(object ns, NetworkAvailabilityEventArgs ne)
                {
                    if (!ne.IsAvailable) return;
                    try { BeginInvoke(new MethodInvoker(delegate { _net.Check(true); })); }
                    catch { }
                };
            };

            // Coming back to the window is when the history is looked at: refresh
            // the missing-file marker, at most every 30 s.
            Activated += delegate { try { CheckCompletedFiles(); } catch { } };

            webBrowser1.ObjectForScripting = new ScriptBridge(this);

            // window.external is only safe while the app page is loaded: a
            // dropped file or a link must not replace it with untrusted HTML
            // that could read cookies or write files.
            webBrowser1.Navigating += delegate(object s, WebBrowserNavigatingEventArgs e)
            {
                string url = "";
                try { url = e.Url == null ? "" : e.Url.AbsoluteUri; } catch { }
                if (!Util.AllowDocumentNavigation(url)) e.Cancel = true;
            };
            webBrowser1.NewWindow += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                e.Cancel = true;
            };

            webBrowser1.DocumentCompleted += delegate
            {
                _documentReady = true;
                try { webBrowser1.Document.InvokeScript("applyTheme", new object[] { _settings.Theme }); }
                catch { }
                ApplyHtmlZoom();
                PushState();
            };

            LoadUiPage();

            FormClosing += OnFormClosing;
        }

        private void LoadUiPage()
        {
            _documentReady = false;
            _lastPushed = null;
            string html = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui.html");
            if (!File.Exists(html))
            {
                Alert("ui.html not found next to the executable:\n" + html,
                      L("msg.missing_resource"), MessageBoxIcon.Error);
                Environment.Exit(1);
                return;
            }
            webBrowser1.DocumentText = File.ReadAllText(html);
        }

        // -----------------------------------------------------------------
        //  Tray
        // -----------------------------------------------------------------
        private void BuildTray()
        {
            Icon trayIcon = null;
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(ico)) trayIcon = new Icon(ico);
            }
            catch { }
            _tray = new NotifyIcon();
            _tray.Icon = (trayIcon != null) ? trayIcon : SystemIcons.Application;
            _tray.Text = L("app.title");
            _tray.Visible = true;

            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add(L("tray.show"), null, delegate { RestoreFromTray(); });
            m.Items.Add(L("tray.pause_all"), null, delegate { BackendPauseAll(); });
            m.Items.Add(L("tray.resume_all"), null, delegate { BackendResumeAll(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L("tray.exit"), null, delegate { ReallyExit(); });
            _tray.ContextMenuStrip = m;
            _tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        internal void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            ForceForeground(Handle);
        }

        // A background process may not take the foreground, so a plain
        // Activate() can leave a window behind the browser. Attaching to
        // the current foreground thread makes the call succeed.
        internal static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            // Best effort: a refused foreground call must never fail the caller.
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != myThread;
                try
                {
                    if (attached) AttachThreadInput(myThread, fgThread, true);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(myThread, fgThread, false);
                }
            }
            catch { }
        }

        // Borderless windows lose the system menu, so Alt+F4 would do nothing.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.F4))
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Custom chrome: ui.html's title bar drives the native window.
        internal void BeginWindowDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        internal void ToggleWindowState()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (WindowState == FormWindowState.Normal)
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bool max = WindowState == FormWindowState.Maximized;
            if (max == _uiMaximized || !_documentReady) return;
            _uiMaximized = max;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    try { webBrowser1.Document.InvokeScript("applyWindowState", new object[] { max }); }
                    catch { }
                }));
            }
            catch { }
        }

        // The 4 px padding around the browser is the resize frame.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == 1) // HTCLIENT
                {
                    int lp = m.LParam.ToInt32();
                    Point p = PointToClient(new Point(
                        (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    bool l = p.X < ResizeBorder;
                    bool r = p.X >= ClientSize.Width - ResizeBorder;
                    bool t = p.Y < ResizeBorder;
                    bool b = p.Y >= ClientSize.Height - ResizeBorder;
                    if (t && l) m.Result = (IntPtr)13;      // HTTOPLEFT
                    else if (t && r) m.Result = (IntPtr)14; // HTTOPRIGHT
                    else if (b && l) m.Result = (IntPtr)16; // HTBOTTOMLEFT
                    else if (b && r) m.Result = (IntPtr)17; // HTBOTTOMRIGHT
                    else if (l) m.Result = (IntPtr)10;      // HTLEFT
                    else if (r) m.Result = (IntPtr)11;      // HTRIGHT
                    else if (t) m.Result = (IntPtr)12;      // HTTOP
                    else if (b) m.Result = (IntPtr)15;      // HTBOTTOM
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void ReallyExit()
        {
            _reallyExit = true;
            if (_neatLink != null) _neatLink.Stop();
            KillAllDownloads();
            SaveConfig();
            SaveStateAndFlush();
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } }
            catch { }
            Close();
        }

        // Children must die with the app, otherwise they keep downloading unseen.
        private void KillAllDownloads()
        {
            foreach (DownloadItem it in _downloads.Snapshot())
            {
                if (DownloadStates.IsActive(it.State))
                    it.ResumeOnLoad = true;
                try { if (it.Proc != null && !it.Proc.HasExited) it.Proc.Kill(); }
                catch { }
            }
            if (_engine != null) _engine.Stop();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyExit) return;

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveConfig();
                SaveStateAndFlush();
                Hide();
                if (_tray != null)
                {
                    _tray.BalloonTipTitle = L("app.title");
                    _tray.BalloonTipText  = L("tray.balloon");
                    _tray.ShowBalloonTip(2000);
                }
            }
            else
            {
                if (_neatLink != null) _neatLink.Stop();
                KillAllDownloads();
                SaveConfig();
                SaveStateAndFlush();
            }
        }

        // -----------------------------------------------------------------
        //  Config
        // -----------------------------------------------------------------
        private void LoadConfig()
        {
            _settings = SettingsStore.Load(_configFile);
            _maxConcurrent = _settings.MaxConcurrent;
        }

        // Restore the last window position, but never off the visible desktop.
        private void ApplySavedWindow()
        {
            if (_settings.WindowWidth < Theme.Px(400) || _settings.WindowHeight < Theme.Px(240)) return;
            Rectangle r = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
            bool visible = false;
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(s.WorkingArea, r);
                if (i.Width >= 80 && i.Height >= 40) { visible = true; break; }
            }
            if (!visible) { r.X = 80; r.Y = 60; }
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
        }

        private void ApplyHtmlZoom()
        {
            float scale = Theme.Scale;
            if (scale < 1.05f) return;
            try
            {
                object target = null;
                try { target = webBrowser1.Document.DomDocument; } catch { }
                IOleCommandTarget cmd = target as IOleCommandTarget;
                if (cmd == null)
                {
                    try { target = webBrowser1.ActiveXInstance; } catch { }
                    cmd = target as IOleCommandTarget;
                }
                if (cmd == null) return;
                Guid group = new Guid("de4ba900-59ca-11cf-9592-444553540000");
                object zoom = (int)Math.Round(scale * 100f);
                int hr = cmd.Exec(ref group, 63, 0, ref zoom, IntPtr.Zero);
                if (hr != 0) Log("dpi: HTML zoom returned " + hr);
            }
            catch (Exception ex) { Log("dpi: " + ex.Message); }
        }

        private void LoadLanguage()
        {
            if (string.IsNullOrEmpty(_settings.Language)) _settings.Language = "en";
            _lang = Lang.Load(Path.Combine(_langDir, "en.txt"));
            Dictionary<string, string> selected = Lang.Load(Path.Combine(_langDir, _settings.Language + ".txt"));
            if (selected.Count == 0 && _settings.Language != "en")
            {
                _settings.Language = "en";
                selected = null;
            }
            if (selected != null) _lang = Lang.Merge(_lang, selected);

            string d;
            _langDirName = (_lang.TryGetValue("dir", out d) && d.Trim().ToLowerInvariant() == "rtl")
                ? "rtl" : "ltr";
            string code;
            if (_lang.TryGetValue("code", out code) && code.Trim().Length > 0)
                _settings.Language = code.Trim();
            _languagesJson = BuildLanguagesJson();
            _langVer++;
        }

        private string BuildLanguagesJson()
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            try
            {
                if (Directory.Exists(_langDir))
                {
                    string[] files = Directory.GetFiles(_langDir, "*.txt");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        Dictionary<string, string> d = Lang.Load(f);
                        string code, name;
                        if (!d.TryGetValue("code", out code) || code.Trim().Length == 0)
                            code = Path.GetFileNameWithoutExtension(f);
                        if (!d.TryGetValue("name", out name) || name.Trim().Length == 0)
                            name = code;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"code\":\"").Append(Aria.JsonEscape(code.Trim()))
                          .Append("\",\"name\":\"").Append(Aria.JsonEscape(name.Trim())).Append("\"}");
                    }
                }
            }
            catch { }
            sb.Append(']');
            return sb.ToString();
        }

        private string L(string key)
        {
            return Lang.Get(_lang, key, key);
        }

        private void Alert(string text, string title, MessageBoxIcon kind)
        {
            MsgBox.Show(this, text, title, kind, _lang, _settings.Theme == "dark");
        }

        private void ApplyLanguage()
        {
            LoadLanguage();
            if (_tray != null && _tray.ContextMenuStrip != null)
            {
                ToolStripItemCollection items = _tray.ContextMenuStrip.Items;
                if (items.Count >= 5)
                {
                    items[0].Text = L("tray.show");
                    items[1].Text = L("tray.pause_all");
                    items[2].Text = L("tray.resume_all");
                    items[4].Text = L("tray.exit");
                }
            }
        }

        public void BackendSaveTheme(string theme)
        {
            _settings.Theme = "light";
            BackColor = Theme.Frame(false);
            SaveConfig();
        }

        // Opens the Windows colour wheel for the page's appearance pickers.
        public string BackendPickColor(string current)
        {
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                d.AnyColor = true;
                Color c;
                if (TryHexColor(current, out c)) d.Color = c;
                if (d.ShowDialog(this) != DialogResult.OK) return "";
                return "#" + d.Color.R.ToString("x2") + d.Color.G.ToString("x2") + d.Color.B.ToString("x2");
            }
        }

        // Column widths and sort order, owned by the page but stored with the host.
        public void BackendSavePrefs(string packed)
        {
            _settings.Prefs = packed == null ? "" : packed.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (_settings.Prefs.Length > 2000) _settings.Prefs = _settings.Prefs.Substring(0, 2000);
            ApplyProgressColors();
            SaveConfig();
        }

        // The page owns these colors; the native progress popup mirrors them.
        private void ApplyProgressColors()
        {
            try
            {
                Color c;
                string p1 = PrefValue("p1"), p2 = PrefValue("p2");
                if (TryHexColor(p1, out c)) SlimBar.StartColor = c;
                if (TryHexColor(p2, out c)) SlimBar.EndColor = c;
                // The page's accent choice tints every native dialog button.
                if (TryHexColor(PrefValue("a"), out c)) Theme.SetAccent(c);
            }
            catch { }
        }

        private string PrefValue(string key)
        {
            if (string.IsNullOrEmpty(_settings.Prefs)) return "";
            string[] parts = _settings.Prefs.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0 || parts[i].Substring(0, eq) != key) continue;
                try { return Uri.UnescapeDataString(parts[i].Substring(eq + 1)); }
                catch { return ""; }
            }
            return "";
        }

        private static bool TryHexColor(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s) || s.Length != 7 || s[0] != '#') return false;
            try
            {
                c = Color.FromArgb(Convert.ToInt32(s.Substring(1, 2), 16),
                                   Convert.ToInt32(s.Substring(3, 2), 16),
                                   Convert.ToInt32(s.Substring(5, 2), 16));
                return true;
            }
            catch { return false; }
        }

        // Removes finished rows from the list; files stay on disk.
        public void BackendClearCompleted(int removeOrphans)
        {
            CleanHistory(removeOrphans);
        }

        public void BackendSaveSettings(int maxConcurrent, int partsPerDownload, int maxSpeedKbps,
                                        string proxy, int clipWatch, int shutdownWhenDone,
                                        int autoSort, string scheduleAt, string language,
                                        string stopAt, int scheduleDaily, string captureExt)
        {
            BackendSaveSettings(maxConcurrent, partsPerDownload, maxSpeedKbps, proxy, clipWatch,
                                shutdownWhenDone, autoSort, scheduleAt, language, stopAt,
                                scheduleDaily, captureExt,
                                _settings.UpdateCheck ? 1 : 0, _settings.UpdateDays,
                                _settings.BlockedHosts, _settings.NamingTemplate,
                                _settings.SpeedLimitKbps, _settings.SpeedLimitFrom,
                                _settings.SpeedLimitTo);
        }

        public void BackendSaveSettings(int maxConcurrent, int partsPerDownload, int maxSpeedKbps,
                                        string proxy, int clipWatch, int shutdownWhenDone,
                                        int autoSort, string scheduleAt, string language,
                                        string stopAt, int scheduleDaily, string captureExt,
                                        int updateCheck, int updateDays, string blockedHosts,
                                        string namingTemplate, long speedLimitKbps,
                                        string speedLimitFrom, string speedLimitTo)
        {
            long spd = maxSpeedKbps;
            if (maxConcurrent < 1) maxConcurrent = 1;
            if (maxConcurrent > 8) maxConcurrent = 8;
            if (partsPerDownload < 1) partsPerDownload = 1;
            if (partsPerDownload > 16) partsPerDownload = 16;
            if (spd < 0) spd = 0;
            _maxConcurrent = maxConcurrent;
            _settings.PartsPerDownload = partsPerDownload;
            _settings.MaxSpeedKbps = spd;
            if (speedLimitKbps < 0) speedLimitKbps = 0;
            _settings.SpeedLimitKbps = speedLimitKbps;
            _settings.SpeedLimitFrom = Util.IsBlank(speedLimitFrom) ? "" : speedLimitFrom.Trim();
            _settings.SpeedLimitFromSecs = Aria.ParseHhmm(_settings.SpeedLimitFrom);
            if (_settings.SpeedLimitFromSecs < 0) _settings.SpeedLimitFrom = "";
            _settings.SpeedLimitTo = Util.IsBlank(speedLimitTo) ? "" : speedLimitTo.Trim();
            _settings.SpeedLimitToSecs = Aria.ParseHhmm(_settings.SpeedLimitTo);
            if (_settings.SpeedLimitToSecs < 0) _settings.SpeedLimitTo = "";
            _settings.Proxy = proxy == null ? "" : proxy.Trim();
            _settings.CaptureExt = NormalizeCaptureExt(captureExt);
            _settings.BlockedHosts = Util.NormalizeHosts(blockedHosts);
            WriteCaptureConfig();
            _settings.ClipboardWatch = clipWatch != 0;
            _settings.ShutdownWhenDone = shutdownWhenDone != 0;
            _settings.AutoSort = autoSort != 0;
            _settings.NamingTemplate = Util.IsBlank(namingTemplate) ? "" : namingTemplate.Trim();
            if (_settings.NamingTemplate.Length > 200)
                _settings.NamingTemplate = _settings.NamingTemplate.Substring(0, 200);
            _settings.ScheduleAt = Util.IsBlank(scheduleAt) ? "" : scheduleAt.Trim();
            _settings.ScheduleSecs = Aria.ParseHhmm(_settings.ScheduleAt);
            if (_settings.ScheduleSecs < 0) _settings.ScheduleAt = "";
            _settings.StopAt = Util.IsBlank(stopAt) ? "" : stopAt.Trim();
            _settings.StopSecs = Aria.ParseHhmm(_settings.StopAt);
            if (_settings.StopSecs < 0) _settings.StopAt = "";
            _settings.ScheduleDaily = scheduleDaily != 0;
            _settings.UpdateCheck = updateCheck != 0;
            if (updateDays < 1) updateDays = 1;
            if (updateDays > 365) updateDays = 365;
            _settings.UpdateDays = updateDays;
            _stopFired = false;
            _speedWindowApplied = false;
            string lang = Util.IsBlank(language) ? _settings.Language : language.Trim();
            bool langChanged = lang != _settings.Language;
            if (langChanged)
            {
                _settings.Language = lang;
                ApplyLanguage();
            }
            SaveConfig();
            ApplyClipWatch();
            TryStartNext();
            PushState();
        }

        public string BackendGetSettings()
        {
            return "{\"maxConcurrent\":" + _maxConcurrent +
                   ",\"partsPerDownload\":" + _settings.PartsPerDownload +
                   ",\"maxSpeedKbps\":" + _settings.MaxSpeedKbps +
                   ",\"proxy\":\"" + Aria.JsonEscape(_settings.Proxy) + "\"" +
                   ",\"captureExt\":\"" + Aria.JsonEscape(_settings.CaptureExt) + "\"" +
                   ",\"blockedHosts\":\"" + Aria.JsonEscape(_settings.BlockedHosts) + "\"" +
                   ",\"clipboardWatch\":" + (_settings.ClipboardWatch ? 1 : 0) +
                   ",\"shutdownWhenDone\":" + (_settings.ShutdownWhenDone ? 1 : 0) +
                   ",\"autoSort\":" + (_settings.AutoSort ? 1 : 0) +
                   ",\"namingTemplate\":\"" + Aria.JsonEscape(_settings.NamingTemplate) + "\"" +
                   ",\"scheduleAt\":\"" + Aria.JsonEscape(_settings.ScheduleAt) + "\"" +
                   ",\"stopAt\":\"" + Aria.JsonEscape(_settings.StopAt) + "\"" +
                   ",\"scheduleDaily\":" + (_settings.ScheduleDaily ? 1 : 0) +
                   ",\"updateCheck\":" + (_settings.UpdateCheck ? 1 : 0) +
                   ",\"updateDays\":" + _settings.UpdateDays +
                   ",\"speedLimitKbps\":" + _settings.SpeedLimitKbps +
                   ",\"speedLimitFrom\":\"" + Aria.JsonEscape(_settings.SpeedLimitFrom) + "\"" +
                   ",\"speedLimitTo\":\"" + Aria.JsonEscape(_settings.SpeedLimitTo) + "\"" +
                   ",\"language\":\"" + Aria.JsonEscape(_settings.Language) + "\"}";
        }

        // Extensions are written into the extension folders next to the exe,
        // where the browser reads capture.json without an app restart.
        private static string NormalizeCaptureExt(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                                         StringSplitOptions.RemoveEmptyEntries);
            List<string> list = new List<string>();
            foreach (string raw in parts)
            {
                string e = raw.Trim().ToLowerInvariant();
                if (e.StartsWith("*.")) e = e.Substring(2);
                else e = e.TrimStart('.');
                if (e.Length < 1 || e.Length > 10) continue;
                bool ok = true;
                for (int i = 0; i < e.Length; i++)
                {
                    char c = e[i];
                    if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) { ok = false; break; }
                }
                if (!ok || list.Contains(e)) continue;
                list.Add(e);
                if (list.Count >= 120) break;
            }
            return string.Join(",", list.ToArray());
        }

        private void WriteCaptureConfig()
        {
            try
            {
                string[] exts = _settings.CaptureExt.Length == 0 ? new string[0] : _settings.CaptureExt.Split(',');
                StringBuilder json = new StringBuilder("{\"ext\":[");
                for (int i = 0; i < exts.Length; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append('"').Append(Aria.JsonEscape(exts[i])).Append('"');
                }
                json.Append("],\"token\":\"").Append(Aria.JsonEscape(_settings.NeatToken)).Append("\"}");

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // A dev checkout keeps the extension folders at the project root;
                // write there too so a source-loaded extension sees the token.
                string sourceRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\.."));
                string[] roots = File.Exists(Path.Combine(sourceRoot, "build.cmd"))
                    ? new string[] { baseDir, sourceRoot }
                    : new string[] { baseDir };
                string[] names = { "zid-crx", "zid-crx109", "zid-xpi" };
                foreach (string root in roots)
                {
                    foreach (string name in names)
                    {
                        string dir = Path.Combine(root, name);
                        if (!Directory.Exists(dir)) continue;
                        string path = Path.Combine(dir, "capture.json");
                        string data = json.ToString();
                        try { if (File.Exists(path) && File.ReadAllText(path) == data) continue; }
                        catch { }
                        File.WriteAllText(path, data);
                        RestrictToCurrentUser(path);
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            _settings.MaxConcurrent = _maxConcurrent;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (b.Width > 0 && b.Height > 0)
            {
                _settings.WindowWidth = b.Width;
                _settings.WindowHeight = b.Height;
                _settings.WindowX = b.X;
                _settings.WindowY = b.Y;
                _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            }
            SettingsStore.Save(_configFile, _settings);
        }

        // -----------------------------------------------------------------
        //  Persistence
        // Lets the browser extension cold-start the app with zid://open.
        private void RegisterProtocol()
        {
            try
            {
                string exe = Application.ExecutablePath;
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\zid"))
                {
                    if (k == null) return;
                    k.SetValue("", "URL:ZyZoom Internet Downloader");
                    k.SetValue("URL Protocol", "");
                    using (RegistryKey icon = k.CreateSubKey("DefaultIcon"))
                        if (icon != null) icon.SetValue("", "\"" + exe + "\",0");
                    using (RegistryKey cmd = k.CreateSubKey(@"shell\open\command"))
                        if (cmd != null) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
                }
                // The pre-rename handler would start an old exe that may be gone.
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\zyzoom"); }
                catch { }
            }
            catch (Exception ex) { Log("protocol: " + ex.Message); }
        }

        // Best-effort diagnostics; Logger caps file size and masks secrets.
        private void Log(string msg)
        {
            Logger.Info(msg);
        }

        // Drop the oldest finished rows once the history grows past the cap.
        // Done audio that still waits for its video is kept for merging.
        private void TrimHistory()
        {
            int removed = HistoryService.Trim(_downloads, HistoryService.MaxItems, DateTime.Now);
            if (removed > 0) Log("history trimmed: " + removed + " old rows removed");
        }

        // Ask once at 100 finished rows, then again every 50 more if kept.
        private void CheckHistoryPrompt()
        {
            if (_historyPrompting || !Visible || WindowState == FormWindowState.Minimized) return;

            int finished = HistoryService.CountFinished(_downloads);
            if (finished < _historyWarnAt) return;

            _historyPrompting = true;
            try
            {
                string text = "You have " + finished + " finished downloads.\r\n\r\n" +
                    "Remove old finished rows? Downloaded files are not deleted.";
                DialogResult r = MsgBox.Confirm(this, text, "Download history",
                    Lang.Get(_lang, "btn.remove", "Remove"),
                    Lang.Get(_lang, "btn.keep", "Keep"),
                    _lang, _settings.Theme == "dark");
                if (r == DialogResult.OK)
                {
                    int removed = CleanHistory(0);
                    Log("history cleaned by user: " + removed + " rows removed");
                    _historyWarnAt = HistoryService.NextPromptAt(finished, true);
                }
                else
                {
                    _historyWarnAt = HistoryService.NextPromptAt(finished, false);
                }
            }
            finally { _historyPrompting = false; }
        }

        // Remove finished rows only; files stay on disk. Pending merge audio stays.
        // With removeOrphans set, leftover partials no row owns are deleted too.
        private int CleanHistory(int removeOrphans)
        {
            int removed = HistoryService.Clean(_downloads);
            int orphans = removeOrphans != 0
                ? HistoryService.RemoveOrphans(_downloads, _downloadFolder) : 0;
            if (removed > 0 || orphans > 0)
            {
                SaveState();
                PushState();
            }
            if (orphans > 0) Log("orphan partial files removed: " + orphans);
            return removed;
        }

        private void SaveState()
        {
            _stateWriter.Save(_stateStore.Capture(_downloads.Snapshot(), _downloads.NextId));
        }

        // Exit and terminal states must be on disk before the process goes away.
        private void SaveStateAndFlush()
        {
            SaveState();
            _stateWriter.Flush(5000);
        }

        private void LoadState()
        {
            StateData data = _stateStore.Load(Log);
            _downloads.NextId = Recovery.Validate(data.Items, _downloadFolder, data.NextId, Log);
            _downloads.AddRange(data.Items);
            TrimHistory();
        }

        // Free bytes on the drive that holds path; 0 when unknown.
        // Cached briefly: the worker checks it several times per second.
        private static readonly Dictionary<string, long> _freeBytes = new Dictionary<string, long>();
        private static readonly Dictionary<string, DateTime> _freeAt = new Dictionary<string, DateTime>();
        private static long FreeSpaceFor(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return 0;
                lock (_freeBytes)
                {
                    long v;
                    DateTime at;
                    if (_freeAt.TryGetValue(root, out at) &&
                        (DateTime.Now - at).TotalSeconds < 5 &&
                        _freeBytes.TryGetValue(root, out v))
                        return v;
                    DriveInfo d = new DriveInfo(root);
                    v = d.IsReady ? d.AvailableFreeSpace : 0;
                    _freeBytes[root] = v;
                    _freeAt[root] = DateTime.Now;
                    return v;
                }
            }
            catch { return 0; }
        }

        // -----------------------------------------------------------------
        //  State -> JS
        // -----------------------------------------------------------------
        // A state change always reaches the grid, even from a worker thread.
        private void OnDownloadStateChanged(DownloadItem item, DownloadState from, DownloadState to)
        {
            PushState();
        }

        public void PushState()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new MethodInvoker(PushState)); } catch { }
                return;
            }
            if (!_documentReady) return;
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            try { if (webBrowser1.Document == null) return; }
            catch { return; }

            _lastPushTick = Environment.TickCount;
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("{\"items\":[");
            bool first = true;

            List<DownloadItem> snapshot = _downloads.Snapshot();

            // One pass instead of FindPair per row (O(n) not O(n^2)).
            Dictionary<string, int> videoKeys = new Dictionary<string, int>();
            foreach (DownloadItem v in snapshot)
            {
                // A muxed row is a complete video too; its paired subtitles are hidden.
                if (v.Merged || (v.Kind != "video" && v.Kind != "muxed") || Util.IsBlank(v.MergeKey)) continue;
                int c;
                videoKeys.TryGetValue(v.MergeKey, out c);
                videoKeys[v.MergeKey] = c + 1;
            }

            int order = 0;
            foreach (DownloadItem i in snapshot)
            {
                if (i.Auto && i.State != DownloadState.Failed && !Util.IsBlank(i.MergeKey))
                {
                    int c;
                    if (videoKeys.TryGetValue(i.MergeKey, out c) && c == 1) continue;
                }
                if (!first) sb.Append(',');
                first = false;

                sb.Append('{')
                  .Append("\"id\":").Append(i.Id).Append(',')
                  .Append("\"fileName\":\"").Append(Aria.JsonEscape(i.FileName)).Append("\",")
                  .Append("\"url\":\"").Append(Aria.JsonEscape(i.Url)).Append("\",")
                  .Append("\"type\":\"").Append(Aria.JsonEscape(i.Type)).Append("\",")
                  .Append("\"size\":").Append(i.Size).Append(',')
                  .Append("\"progress\":").Append(i.Progress).Append(',')
                  .Append("\"speed\":").Append(i.Speed).Append(',')
                  .Append("\"priority\":").Append(i.Priority).Append(',')
                  .Append("\"order\":").Append(order++).Append(',')
                  .Append("\"status\":\"").Append(Aria.JsonEscape(i.Status)).Append("\",")
                  .Append("\"eta\":\"").Append(Aria.JsonEscape(i.Eta)).Append("\",")
                  .Append("\"checksum\":\"").Append(Aria.JsonEscape(i.Checksum)).Append("\",")
                  .Append("\"date\":\"").Append(i.Date.ToString("yyyy-MM-dd HH:mm")).Append("\"")
                  .Append('}');
            }
            sb.Append("],\"diskFree\":").Append(FreeSpaceFor(_downloadFolder))
              .Append(",\"settings\":{\"maxConcurrent\":").Append(_maxConcurrent)
              .Append(",\"partsPerDownload\":").Append(_settings.PartsPerDownload)
              .Append(",\"maxSpeedKbps\":").Append(_settings.MaxSpeedKbps)
              .Append(",\"proxy\":\"").Append(Aria.JsonEscape(_settings.Proxy))
              .Append("\",\"captureExt\":\"").Append(Aria.JsonEscape(_settings.CaptureExt))
              .Append("\",\"clipboardWatch\":").Append(_settings.ClipboardWatch ? 1 : 0)
              .Append(",\"shutdownWhenDone\":").Append(_settings.ShutdownWhenDone ? 1 : 0)
              .Append(",\"autoSort\":").Append(_settings.AutoSort ? 1 : 0)
              .Append(",\"namingTemplate\":\"").Append(Aria.JsonEscape(_settings.NamingTemplate))
              .Append("\",\"scheduleAt\":\"").Append(Aria.JsonEscape(_settings.ScheduleAt))
              .Append("\",\"stopAt\":\"").Append(Aria.JsonEscape(_settings.StopAt))
              .Append("\",\"scheduleDaily\":").Append(_settings.ScheduleDaily ? 1 : 0)
              .Append(",\"speedLimitKbps\":").Append(_settings.SpeedLimitKbps)
              .Append(",\"speedLimitFrom\":\"").Append(Aria.JsonEscape(_settings.SpeedLimitFrom))
              .Append("\",\"speedLimitTo\":\"").Append(Aria.JsonEscape(_settings.SpeedLimitTo))
              .Append("\",\"language\":\"").Append(Aria.JsonEscape(_settings.Language))
              .Append("\",\"prefs\":\"").Append(Aria.JsonEscape(_settings.Prefs))
              .Append("\",\"lastFolder\":\"").Append(Aria.JsonEscape(_settings.LastFolder))
               .Append("\"},\"langDir\":\"").Append(_langDirName)
               .Append("\",\"langVer\":").Append(_langVer)
               .Append(",\"languages\":").Append(_languagesJson)
              .Append(",\"lang\":{");
            bool firstLang = true;
            foreach (KeyValuePair<string, string> kv in _lang)
            {
                if (!firstLang) sb.Append(',');
                firstLang = false;
                sb.Append('"').Append(Aria.JsonEscape(kv.Key)).Append("\":\"")
                  .Append(Aria.JsonEscape(kv.Value)).Append('"');
            }
            sb.Append("}}");
            string json = sb.ToString();
            if (json == _lastPushed) return;
            _lastPushed = json;

            try { webBrowser1.Document.InvokeScript("renderState", new object[] { json }); }
            catch { }
        }

        // Settings are sent from the page through the document title to avoid
        // COM argument marshaling (IE rejects it): "__SET__mc|parts|speedKbps".
        private void CheckTitleChannel()
        {
            if (!_documentReady) return;
            string title;
            try { title = webBrowser1.Document.Title; } catch { return; }
            if (title == null || title.IndexOf("__SET__", StringComparison.Ordinal) != 0) return;

            int mc = _maxConcurrent, pr = _settings.PartsPerDownload, spd = (int)_settings.MaxSpeedKbps;
            string proxy = _settings.Proxy, sched = _settings.ScheduleAt;
            string stop = _settings.StopAt;
            int clip = _settings.ClipboardWatch ? 1 : 0, shut = _settings.ShutdownWhenDone ? 1 : 0;
            int auto = _settings.AutoSort ? 1 : 0, daily = _settings.ScheduleDaily ? 1 : 0;
            int updateCheck = _settings.UpdateCheck ? 1 : 0, updateDays = _settings.UpdateDays;
            string lang = _settings.Language, capture = _settings.CaptureExt;
            string blocked = _settings.BlockedHosts;
            string naming = _settings.NamingTemplate;
            int sl = (int)_settings.SpeedLimitKbps;
            string slFrom = _settings.SpeedLimitFrom, slTo = _settings.SpeedLimitTo;
            string[] parts = title.Substring(7).Split('|');
            int v;
            if (parts.Length > 0 && int.TryParse(parts[0], out v)) mc = v;
            if (parts.Length > 1 && int.TryParse(parts[1], out v)) pr = v;
            if (parts.Length > 2 && int.TryParse(parts[2], out v)) spd = v;
            if (parts.Length > 3) { try { proxy = Uri.UnescapeDataString(parts[3]); } catch { } }
            if (parts.Length > 4 && int.TryParse(parts[4], out v)) clip = v;
            if (parts.Length > 5 && int.TryParse(parts[5], out v)) shut = v;
            if (parts.Length > 6 && int.TryParse(parts[6], out v)) auto = v;
            if (parts.Length > 7) { try { sched = Uri.UnescapeDataString(parts[7]); } catch { } }
            if (parts.Length > 8) { try { lang = Uri.UnescapeDataString(parts[8]); } catch { } }
            if (parts.Length > 9) { try { stop = Uri.UnescapeDataString(parts[9]); } catch { } }
            if (parts.Length > 10 && int.TryParse(parts[10], out v)) daily = v;
            if (parts.Length > 11) { try { capture = Uri.UnescapeDataString(parts[11]); } catch { } }
            if (parts.Length > 12 && int.TryParse(parts[12], out v)) updateCheck = v;
            if (parts.Length > 13 && int.TryParse(parts[13], out v)) updateDays = v;
            if (parts.Length > 14) { try { blocked = Uri.UnescapeDataString(parts[14]); } catch { } }
            if (parts.Length > 15) { try { naming = Uri.UnescapeDataString(parts[15]); } catch { } }
            if (parts.Length > 16 && int.TryParse(parts[16], out v)) sl = v;
            if (parts.Length > 17) { try { slFrom = Uri.UnescapeDataString(parts[17]); } catch { } }
            if (parts.Length > 18) { try { slTo = Uri.UnescapeDataString(parts[18]); } catch { } }

            BackendSaveSettings(mc, pr, spd, proxy, clip, shut, auto, sched, lang, stop, daily, capture,
                                updateCheck, updateDays, blocked, naming, sl, slFrom, slTo);
            try { webBrowser1.Document.InvokeScript("resetTitle"); } catch { }
        }

        // -----------------------------------------------------------------
        //  Add / Edit
        // -----------------------------------------------------------------
        public void BackendAdd(string url, string fileName, string saveFolder, string cookie,
                               string parts, string speedKbps)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, true);
        }

        public void BackendAdd(string url, string fileName, string saveFolder, string cookie,
                               string parts, string speedKbps,
                               string headers, string checksum, string mirror, string proxy)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, true,
                        "", "", "", false, "", headers, checksum, mirror, proxy);
        }

        // The Add dialog asks for a cheap HEAD check; it runs off the UI thread
        // and the reply is pushed back with InvokeScript so the dialog never waits.
        public void BackendProbeUrl(string url, int token)
        {
            if (!AddService.IsSupportedUrl(url)) return;
            Thread t = new Thread(delegate()
            {
                try
                {
                    string json = MetadataProbe.Probe(url);
                    if (json.Length == 0) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        try
                        {
                            webBrowser1.Document.InvokeScript(
                                "onProbeResult", new object[] { token, json });
                        }
                        catch { }
                    }));
                }
                catch (Exception ex) { Program.ReportCrash("probe", ex); }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, startNow, "", "", "");
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow,
                                 string referer, string userAgent)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, startNow,
                        referer, userAgent, "", false, "");
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow,
                                 string referer, string userAgent, string kind)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, startNow,
                        referer, userAgent, kind, false, "");
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow,
                                 string referer, string userAgent, string kind, bool auto)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, startNow,
                        referer, userAgent, kind, auto, "");
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow,
                                 string referer, string userAgent, string kind, bool auto,
                                 string pairKey)
        {
            AddDownload(url, fileName, saveFolder, cookie, parts, speedKbps, startNow,
                        referer, userAgent, kind, auto, pairKey, "", "", "", "");
        }

        private void AddDownload(string url, string fileName, string saveFolder, string cookie,
                                 string parts, string speedKbps, bool startNow,
                                 string referer, string userAgent, string kind, bool auto,
                                 string pairKey, string headers, string checksum, string mirror,
                                 string proxy)
        {
            if (!AddService.IsSupportedUrl(url)) return;

            // Remember the folder a person typed, not the auto capture default.
            if (!auto && !Util.IsBlank(saveFolder) && saveFolder.Trim() != _settings.LastFolder)
            {
                _settings.LastFolder = saveFolder.Trim();
                SaveConfig();
            }

            fileName = AddService.FileName(url, fileName, _downloads.NextId);
            string type = AddService.TypeFor(fileName);
            string folder;
            if (Util.IsBlank(saveFolder) && !Util.IsBlank(_settings.NamingTemplate))
                folder = AddService.TemplateFolder(_downloadFolder, _settings.NamingTemplate,
                                                   url, type, ref fileName);
            else
                folder = AddService.PrepareFolder(_downloadFolder, saveFolder,
                                                  _settings.AutoSort, type);
            string local = Path.Combine(folder, fileName);

            // Manual adds ask; automatic (browser/clipboard) adds keep the old
            // auto-rename so a capture never blocks on a dialog.
            DownloadItem dup = null;
            bool sameSource = false;
            if (!auto)
            {
                dup = _downloads.FindByUrl(url);
                if (dup == null) dup = _downloads.FindByPath(local);
                sameSource = dup != null && Util.SameUrl(dup.Url, url);
                if (dup != null || File.Exists(local))
                {
                    switch (AskDuplicate(dup, sameSource, local))
                    {
                        case DuplicateAction.Cancel:
                            return;
                        case DuplicateAction.Resume:
                            ResumeDuplicate(dup);
                            return;
                        case DuplicateAction.Replace:
                            ReplaceDuplicate(dup, local);
                            break;
                    }
                }
            }

            local = AddService.UniquePath(folder, fileName, new Predicate<string>(PathTaken));

            string cookieValue = Util.IsBlank(cookie) ? GetIeCookie(url) : cookie.Trim();
            DownloadItem item = AddService.CreateItem(_downloads.TakeNextId(), url, local, type,
                                                      cookieValue, referer, userAgent, kind, auto,
                                                      pairKey, headers, checksum, mirror, proxy,
                                                      parts, speedKbps);
            _downloads.Queue(item);
            item.Eta        = "Connecting...";
            item.Date       = DateTime.Now;
            item.WantRun    = true;

            bool hls = Playlist.IsHls(url);
            bool dash = Playlist.IsDash(url);
            if (!hls && !dash && item.Kind == "playlist" &&
                url.IndexOf(".f4m", StringComparison.OrdinalIgnoreCase) < 0)
                hls = true;
            if (hls || dash)
            {
                item.Kind = hls ? "hls" : "dash";
                string np = AddService.HlsOutputPath(folder, item.FileName,
                                                     new Predicate<string>(PathTaken));
                item.FileName = Path.GetFileName(np);
                item.LocalPath = np;
                item.Type = "MP4";
            }

            _downloads.Add(item);
            TrimHistory();
            AddStart how = AddService.StartKind(item, startNow);
            if (how == AddStart.WaitForVideo)
            {
                // Hidden audio waits until its video is finished and its header
                // shows no audio track; TryStartNext only picks up "Queued".
                item.WantRun = false;
                _downloads.Wait(item);
                item.Eta = "Waiting for video";
            }
            else if (how == AddStart.Paused)
            {
                item.WantRun = false;
                _downloads.Pause(item);
                item.Eta = "Paused";
            }
            PushState();
            SaveState();
            // Auto subtitles start at once; only the hidden audio waits.
            if (how == AddStart.Start) EnqueueOrStart(item);
        }

        private enum DuplicateAction { Cancel, Resume, Keep, Replace }

        // A row can own a path before the file exists (queued, paused, or the
        // first capture is still connecting), so disk checks alone are not enough.
        private bool PathTaken(string path)
        {
            return _downloads.FindByPath(path) != null;
        }

        private DuplicateAction AskDuplicate(DownloadItem dup, bool sameSource, string local)
        {
            List<string> labels = new List<string>();
            if (sameSource)
                labels.Add(L(dup.State == DownloadState.Completed ? "dup.open" : "dup.resume"));
            labels.Add(L("dup.keep"));
            labels.Add(L("dup.replace"));
            labels.Add(L("dup.cancel"));

            string text = (sameSource ? L("dup.url") : L("dup.file")) + "\n\n" +
                          (sameSource ? L("dup.hint") : L("dup.hint_file")) + "\n\n" + local;
            int pick = MsgBox.Choose(this, text, L("dup.title"), labels.ToArray(), _lang, _settings.Theme == "dark");
            if (pick < 0) return DuplicateAction.Cancel;
            int keep = sameSource ? 1 : 0;
            if (pick == keep) return DuplicateAction.Keep;
            if (pick == keep + 1) return DuplicateAction.Replace;
            return pick == 0 ? DuplicateAction.Resume : DuplicateAction.Cancel;
        }

        // Resume continues the existing row; a finished one is just opened.
        private void ResumeDuplicate(DownloadItem dup)
        {
            if (dup == null) return;
            if (dup.State == DownloadState.Completed) { BackendOpen(dup.Id); return; }
            BackendResume(dup.Id);
        }

        // Replace: stop the old worker first, then remove its row and files.
        private void ReplaceDuplicate(DownloadItem dup, string local)
        {
            if (dup != null)
            {
                dup.WantRun = false;
                dup.Abort = true;
                ForgetGid(dup);
                Thread t = dup.Thread;
                if (t != null && t.IsAlive) { try { t.Join(5000); } catch { } }
                BackendRemoveItem(dup.Id, 1);
            }
            try { if (File.Exists(local)) File.Delete(local); }
            catch (Exception ex) { Log("replace file: " + ex.Message); }
        }

        public void BackendEdit(int id, string url, string fileName, string saveFolder, string cookie,
                                string parts, string speedKbps,
                                string headers, string checksum, string mirror, string proxy,
                                string startAt)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;

            string oldPath = it.LocalPath;

            if (DownloadStates.IsActive(it.State) ||
                it.State == DownloadState.WaitingNetwork)
                BackendPause(id);

            // Let the worker release the old file before moving the partial data.
            Thread worker = it.Thread;
            if (worker != null && worker.IsAlive)
            {
                try { worker.Join(5000); } catch { }
            }
            ForgetGid(it);

            if (!Util.IsBlank(url))
            {
                try
                {
                    Uri u;
                    if (Uri.TryCreate(url, UriKind.Absolute, out u) &&
                        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeFtp))
                        it.Url = url;
                }
                catch { }
            }
            if (!Util.IsBlank(fileName)) it.FileName = Util.SanitizeFileName(Path.GetFileName(fileName));
            it.Cookie = Util.IsBlank(cookie) ? GetIeCookie(it.Url) : cookie.Trim();
            it.Headers  = Util.NormalizeHeaders(headers);
            it.Checksum = Util.NormalizeChecksum(checksum);
            it.Mirror   = Util.NormalizeMirror(mirror, it.Url);
            it.Proxy    = Util.IsBlank(proxy) ? "" : proxy.Trim();

            string folder = Util.IsBlank(saveFolder) ? _downloadFolder : saveFolder;
            try { Directory.CreateDirectory(folder); } catch { folder = _downloadFolder; }
            string target = Path.Combine(folder, it.FileName);
            if (it.Merged)
            {
                it.Merged = false;
                if (string.Equals(target, it.LocalPath, StringComparison.OrdinalIgnoreCase))
                {
                    string b = Path.GetFileNameWithoutExtension(it.FileName);
                    string e = Path.GetExtension(it.FileName);
                    int n = 2;
                    do { target = Path.Combine(folder, b + " (" + n + ")" + e); n++; }
                    while (File.Exists(target));
                    it.FileName = Path.GetFileName(target);
                }
            }
            if ((worker == null || !worker.IsAlive) &&
                !string.Equals(oldPath, target, StringComparison.OrdinalIgnoreCase))
            {
                MovePartial(oldPath, target);
                try { if (Directory.Exists(oldPath + ".zidseg")) Directory.Delete(oldPath + ".zidseg", true); } catch { }
            }
            it.LocalPath = target;

            string ext = Path.GetExtension(it.FileName);
            it.Type = string.IsNullOrEmpty(ext) ? "FILE" : ext.TrimStart('.').ToUpperInvariant();

            int pv;
            it.MaxConn = (int.TryParse(parts, out pv) && pv >= 1 && pv <= 16) ? pv : 0;
            long sv;
            it.SpeedKbps = (long.TryParse(speedKbps, out sv) && sv >= 0) ? sv : 0;

            it.StartAt = SchedulePolicy.NextAt(startAt, DateTime.Now);
            if (it.StartAt != DateTime.MinValue)
            {
                _downloads.Queue(it);
                it.Eta     = "Scheduled for " + it.StartAt.ToString("HH:mm");
                it.WantRun = false;
            }
            else
            {
                _downloads.Pause(it);
                it.Eta     = "Paused";
                it.WantRun = false;
            }
            TryStartNext();
            PushState();
            SaveState();
        }

        // -----------------------------------------------------------------
        //  Download pipeline
        // -----------------------------------------------------------------
        private void EnqueueOrStart(DownloadItem item)
        {
            if (_queue.TryStart(SchedulePending()))
            {
                StartDownload(item);
                return;
            }
            if (item.State != DownloadState.Queued) _downloads.Queue(item);
            item.Eta = SchedulePending() ? "Scheduled for " + _settings.ScheduleAt : "Queued";
            item.WantRun = false;
            PushState();
        }

        private void TryStartNext()
        {
            DownloadItem next;
            if (!_queue.TryClaimNext(_downloads.Snapshot(), SchedulePending(), DateTime.Now, out next)) return;
            next.WantRun = true;
            next.Abort = false;
            StartDownload(next);
        }

        private bool SchedulePending()
        {
            return SchedulePolicy.IsPending(_settings.ScheduleSecs, _settings.StopSecs, _settings.ScheduleDaily,
                                            SchedulePolicy.SecondsOfDay(DateTime.Now));
        }

        private bool StopReached(int now)
        {
            return SchedulePolicy.StopReached(_settings.ScheduleSecs, _settings.StopSecs, _settings.ScheduleDaily, now);
        }

        private bool HasQueued()
        {
            return _queue.HasQueued(_downloads.Snapshot());
        }

        private void StartDownload(DownloadItem item)
        {
            ApplySavedCredential(item);
            int gen = ++item.Generation;
            item.Abort      = false;
            item.Speed      = 0;
            item.ActiveConnections = 0;
            _downloads.Start(item);
            item.Eta        = "Connecting...";

            Thread t = new Thread(delegate ()
            {
                try { _worker.Run(item, gen); }
                catch (Exception ex) { Program.ReportCrash("download worker", ex); }
            });
            t.IsBackground = true;
            item.Thread = t;
            t.Start();
            SaveState();
        }

        // Downloads are delegated to the aria2 daemon (x8 connections, resume,
        // retry, TLS, cookies). One shared process owns the queue.
        private bool EnsureEngine()
        {
            if (_engines.Ensure())
            {
                ReconcileOnce(_engine);
                return true;
            }
            return false;
        }

        // Runs once, from the first worker that connects. The daemon may still
        // hold tasks from a previous run; adopt or remove them before any new
        // Add so there is never a duplicate or an invisible orphan.
        private void ReconcileOnce(IDownloadEngine engine)
        {
            if (_reconciled) return;
            lock (_reconcileLock)
            {
                if (_reconciled) return;
                _reconciled = true;
                try
                {
                    ReconcileReport report = Reconciler.Run(_downloads.Snapshot(), engine, Log);
                    if (report.Changed > 0)
                    {
                        Log("reconcile: " + report.Linked + " linked, " +
                            report.Completed + " completed, " + report.Failed + " failed, " +
                            report.Orphans + " stale/orphan removed");
                        SaveState();
                        PushState();
                    }
                }
                catch (Exception ex) { Log("reconcile: " + ex.Message); }
            }
        }

        // Drop the daemon's copy of a download before restarting or editing it.
        private void ForgetGid(DownloadItem it)
        {
            string gid = it.EngineId;
            it.EngineId = null;
            IDownloadEngine engine = _engine;
            if (gid == null || engine == null) return;
            try { engine.Remove(gid); } catch { }
        }

        // ---- IWorkerHost ------------------------------------------------
        DownloadManager IWorkerHost.Downloads { get { return _downloads; } }
        QueueManager IWorkerHost.Queue { get { return _queue; } }
        string IWorkerHost.AriaExe { get { return _ariaExe; } }
        string IWorkerHost.ToolsDir { get { return _toolsDir; } }
        string IWorkerHost.DownloadFolder { get { return _downloadFolder; } }
        int IWorkerHost.PartsPerDownload { get { return _settings.PartsPerDownload; } }
        long IWorkerHost.MaxSpeedKbps { get { return EffectiveSpeedKbps(); } }
        string IWorkerHost.Proxy { get { return _settings.Proxy; } }
        IDownloadEngine IWorkerHost.Engine { get { return _engine; } }
        string IWorkerHost.EngineError { get { return _engineError; } }
        bool IWorkerHost.EnsureEngine() { return EnsureEngine(); }
        long IWorkerHost.FreeSpaceFor(string path) { return FreeSpaceFor(path); }
        void IWorkerHost.ForgetGid(DownloadItem item) { ForgetGid(item); }
        bool IWorkerHost.CheckVideoAudio(DownloadItem video) { return CheckVideoAudio(video); }
        bool IWorkerHost.AskCredentials(DownloadItem item) { return PromptCredentials(item); }
        void IWorkerHost.TryMerge(DownloadItem item) { TryMerge(item); }
        void IWorkerHost.SaveState() { SaveState(); }
        void IWorkerHost.SaveStateAndFlush() { SaveStateAndFlush(); }
        void IWorkerHost.PushState() { PushState(); }
        void IWorkerHost.TryStartNext() { TryStartNext(); }
        void IWorkerHost.Log(string message) { Log(message); }
        void IWorkerHost.ReportCrash(string context, Exception ex)
        {
            Program.ReportCrash(context, ex);
        }

        // ---- INetworkHost -----------------------------------------------
        List<DownloadItem> INetworkHost.Snapshot() { return _downloads.Snapshot(); }
        void INetworkHost.ResumeAfterWait(DownloadItem item) { ResumeItem(item, false); }
        int INetworkHost.StaggerDelay(int index)
        {
            return NetworkMonitor.StaggerMs(index, Environment.TickCount);
        }
        bool INetworkHost.NetworkAvailable() { return NetworkMonitor.IsAvailable(); }
        bool INetworkHost.ProbeConnection(string url, int timeoutMs)
        {
            return NetworkMonitor.Probe(url, timeoutMs);
        }
        void INetworkHost.PushState() { PushState(); }
        void INetworkHost.Log(string message) { Log(message); }

        // YouTube sends video-only and audio-only streams as separate rows.
        // Merge them once both are Done; keep the pair when it is ambiguous.
        // shortcut: only MP4/M4A is merged; WebM/Opus needs a different muxer.
        private void TryMerge(DownloadItem item)
        {
            if (item == null || item.Merged) return;
            if (Util.IsBlank(item.Kind) || Util.IsBlank(item.MergeKey)) return;
            if (item.Kind != "video" && item.Kind != "audio") return;
            if (item.Kind == "video")
            {
                if (item.State != DownloadState.Completed && item.State != DownloadState.Merging) return;
            }
            else if (item.State != DownloadState.Completed) return;

            DownloadItem video;
            DownloadItem audio;
            lock (_lock)
            {
                if (item.Merged) return;
                MergePairResult pair = MediaService.FindMergePair(_downloads.Snapshot(), item, out video, out audio);
                if (pair == MergePairResult.AmbiguousVideo)
                {
                    Log("merge skipped: more than one video for " + item.MergeKey);
                    return;
                }
                if (pair == MergePairResult.AmbiguousAudio)
                {
                    Log("merge skipped: more than one audio for " + item.MergeKey);
                    return;
                }
                if (pair != MergePairResult.Ready) return;
                if (!MediaService.IsMp4(video.LocalPath) || !MediaService.IsM4a(audio.LocalPath))
                {
                    Log("merge skipped: " + video.FileName + " + " + audio.FileName);
                    return;
                }
                video.Merged = true;
                audio.Merged = true;
                _downloads.Merge(video);
                video.Eta = "Muxing...";
                video.Progress = 99;
            }
            PushState();

            string outPath = MediaService.MergeOutputPath(video.LocalPath);
            MergeFileResult merged = MediaService.MergeFiles(
                video.LocalPath, audio.LocalPath, outPath, new MuxFiles(Mp4Mux.Merge), Log);
            if (!merged.Ok)
            {
                lock (_lock) { video.Merged = false; audio.Merged = false; }
                _downloads.Fail(video);
                video.Eta = "Merge failed: " + merged.Error;
                Log((merged.SaveFailed ? "merge save '" : "merge '") + video.FileName + "': " + merged.Error);
                SaveStateAndFlush();
                PushState();
                return;
            }

            try { if (File.Exists(audio.LocalPath)) File.Delete(audio.LocalPath); }
            catch (Exception ex) { Log("merge audio cleanup '" + audio.FileName + "': " + ex.Message); }

            try
            {
                BeginInvoke(new MethodInvoker(delegate ()
                {
                    lock (_lock)
                    {
                        video.LocalPath = outPath;
                        video.FileName  = Path.GetFileName(outPath);
                        video.Type      = "MP4";
                        try { video.Size = new FileInfo(outPath).Length; } catch { }
                        video.Progress  = 100;
                        _downloads.Complete(video);
                        video.Eta       = "Done";
                        video.Merged    = true;
                        _downloads.Remove(audio);
                    }
                    PushState();
                    SaveStateAndFlush();
                }));
            }
            catch
            {
                lock (_lock)
                {
                    video.LocalPath = outPath;
                    video.FileName  = Path.GetFileName(outPath);
                    video.Type      = "MP4";
                    audio.Eta       = "Merged";
                }
                SaveStateAndFlush();
            }
        }

        private DownloadItem FindPair(DownloadItem item, string kind)
        {
            return MediaService.FindPair(_downloads.Snapshot(), item, kind);
        }

        // Runs once the video is fully downloaded. Returns true when the hidden
        // audio was released to download now.
        private bool CheckVideoAudio(DownloadItem video)
        {
            if (video == null || !MediaService.IsMp4(video.LocalPath)) return false;
            DownloadItem audio = FindPair(video, "audio");
            if (audio == null || !audio.Auto || audio.State != DownloadState.Waiting) return false;

            bool hasVideo, hasAudio;
            bool known = Mp4Mux.TryReadTrackKinds(video.LocalPath, out hasVideo, out hasAudio);
            if (known && hasAudio)
            {
                Log("merge skipped: video header already has audio; dropped " + audio.FileName);
                DropAudio(audio);
                return false;
            }
            Log("video header " + (known ? "has no audio" : "unreadable") +
                "; downloading " + audio.FileName);

            _downloads.Queue(audio);
            audio.Eta    = "Queued";
            audio.WantRun = true;
            EnqueueOrStart(audio);
            return true;
        }

        private void DropAudio(DownloadItem audio)
        {
            if (audio == null) return;
            audio.Skip = true;
            audio.Abort = true;
            audio.ResumeOnLoad = false;
            if (audio.State == DownloadState.Completed)
            {
                try { if (File.Exists(audio.LocalPath)) File.Delete(audio.LocalPath); } catch { }
            }
            try
            {
                BeginInvoke(new MethodInvoker(delegate ()
                {
                    _downloads.Remove(audio);
                    PushState();
                    SaveStateAndFlush();
                }));
            }
            catch
            {
                _downloads.Remove(audio);
                SaveStateAndFlush();
            }
            Log("merge skipped: video already has audio; dropped " + audio.FileName);
        }

        // Keeps aria2's partial file and control file with a renamed/moved item.
        private void MovePartial(string oldPath, string newPath)
        {
            if (Util.IsBlank(oldPath) || Util.IsBlank(newPath)) return;
            try
            {
                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
                string oldCtl = oldPath + ".aria2";
                string newCtl = newPath + ".aria2";
                if (File.Exists(oldCtl) && !File.Exists(newCtl))
                    File.Move(oldCtl, newCtl);
            }
            catch (Exception ex) { Log("move partial: " + ex.Message); }
        }

        private void MergeSweepAsync()
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    List<DownloadItem> snap;
                    snap = _downloads.Snapshot();
                    foreach (DownloadItem it in snap)
                        if (it.State == DownloadState.Completed) TryMerge(it);
                }
                catch (Exception ex)
                {
                    Log("merge sweep: " + ex.Message);
                    Program.ReportCrash("merge sweep", ex);
                }
            });
            t.IsBackground = true;
            t.Name = "Merge sweep";
            t.Start();
        }

        // The input file holds cookies/proxy/auth; keep it readable by this user only.
        private static void RestrictToCurrentUser(string path)
        {
            try
            {
                FileSecurity sec = File.GetAccessControl(path);
                sec.SetAccessRuleProtection(true, false);
                string user = Environment.UserDomainName + "\\" + Environment.UserName;
                sec.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(path, sec);
            }
            catch { }
        }

        [DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool InternetGetCookie(string url, string cookieName,
                                                     StringBuilder cookieData, ref int size);

        // Picks up the session cookies the user already has in Internet Explorer.
        private static string GetIeCookie(string url)
        {
            try
            {
                int size = 4096;
                StringBuilder sb = new StringBuilder(size);
                if (InternetGetCookie(url, null, sb, ref size)) return sb.ToString();
                if (size > 0 && size <= 1024 * 1024)
                {
                    sb = new StringBuilder(size);
                    if (InternetGetCookie(url, null, sb, ref size)) return sb.ToString();
                }
            }
            catch { }
            return "";
        }


        // Starts a worker only after any previous worker has fully exited.
        private void ResumeItem(DownloadItem it, bool fromScratch)
        {
            it.WantRun = true;
            it.Abort   = true;          // ask any current worker to stop
            it.RetryCount = 0;          // a manual resume/restart gets a fresh budget
            it.AuthPrompted = false;    // and may ask for credentials again

            Thread old = it.Thread;
            if (old != null && old.IsAlive)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        old.Join(8000);
                        BeginWorker(it, fromScratch);
                    }
                    catch (Exception ex)
                    {
                        Log("resume worker: " + ex.Message);
                        Program.ReportCrash("resume " + it.FileName, ex);
                    }
                });
            }
            else
            {
                BeginWorker(it, fromScratch);
            }
        }

        private void BeginWorker(DownloadItem it, bool fromScratch)
        {
            if (!it.WantRun) return;
            if (fromScratch && it.Merged) return;

            if (fromScratch)
            {
                ForgetGid(it);
                try { if (File.Exists(it.LocalPath)) File.Delete(it.LocalPath); } catch { }
                try { if (File.Exists(it.LocalPath + ".aria2")) File.Delete(it.LocalPath + ".aria2"); } catch { }
                try { if (Directory.Exists(it.LocalPath + ".zidseg")) Directory.Delete(it.LocalPath + ".zidseg", true); } catch { }
                it.Progress      = 0;
                it.BytesReceived = 0;
            }
            EnqueueOrStart(it);
        }

        // -----------------------------------------------------------------
        //  Row actions
        // -----------------------------------------------------------------
        public void BackendPause(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;

            it.WantRun      = false;
            it.Abort        = true;
            it.Speed        = 0;
            it.ActiveConnections = 0;
            it.ResumeOnLoad = false;
            if (DownloadStates.IsActive(it.State) ||
                it.State == DownloadState.WaitingNetwork)
            {
                _downloads.Pause(it);
                it.Eta    = "Paused";
            }
            PushState();
            SaveState();
        }

        public void BackendResume(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            if (DownloadStates.IsActive(it.State)) return;
            if (it.State == DownloadState.Completed) return;

            it.RefreshPending = false;
            ResumeItem(it, false);
            PushState();
        }

        // Some servers reject or throttle multi-connection requests; one
        // connection is the usual fix for 403/416/stream errors.
        public void BackendRetrySingle(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            it.MaxConn = 1;
            BackendRestart(id);
        }

        // Context menu entry: set credentials for a failed 401/407 row.
        public void BackendSetCredentials(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null || !PromptCredentials(it)) return;
            it.AuthPrompted = true;
            BackendRestart(id);
        }

        // Called by the worker thread; the dialog must run on the UI thread.
        private bool PromptCredentials(DownloadItem item)
        {
            if (item == null) return false;
            if (InvokeRequired)
            {
                bool ok = false;
                try { Invoke(new MethodInvoker(delegate { ok = PromptCredentials(item); })); }
                catch { return false; }
                return ok;
            }

            string saved = item.AuthProxy ? null : SettingsStore.SavedCredential(_settings, item.Url);
            string user = item.AuthUser, pass = item.AuthPass;
            if (Util.IsBlank(user) && saved != null)
            {
                int nl = saved.IndexOf('\n');
                user = nl < 0 ? saved : saved.Substring(0, nl);
                pass = nl < 0 ? "" : saved.Substring(nl + 1);
            }

            using (CredentialsForm f = new CredentialsForm(item.Url, user, pass, saved != null,
                                                           !item.AuthProxy, _lang,
                                                           _settings.Theme == "dark"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                item.AuthUser = f.UserText;
                item.AuthPass = f.PassText;
                bool had = saved != null;
                if (f.Remember)
                {
                    SettingsStore.RememberCredential(_settings, item.Url, f.UserText, f.PassText);
                    SaveConfig();
                }
                else if (had)
                {
                    SettingsStore.ForgetCredential(_settings, item.Url);
                    SaveConfig();
                }
                return true;
            }
        }

        // A saved site credential starts the download without another 401.
        private void ApplySavedCredential(DownloadItem item)
        {
            if (item == null || item.AuthProxy || !Util.IsBlank(item.AuthUser)) return;
            string saved = SettingsStore.SavedCredential(_settings, item.Url);
            if (saved == null) return;
            int nl = saved.IndexOf('\n');
            item.AuthUser = nl < 0 ? saved : saved.Substring(0, nl);
            item.AuthPass = nl < 0 ? "" : saved.Substring(nl + 1);
        }

        public void BackendForgetCredentials()
        {
            SettingsStore.ForgetAllCredentials(_settings);
            SaveConfig();
        }

        public void BackendRestart(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            if (it.Merged)
            {
                Alert(L("msg.merged_restart"), L("app.title"), MessageBoxIcon.Information);
                return;
            }

            it.RefreshPending = false;
            ResumeItem(it, true);
            PushState();
        }

        // Opens the page this link came from and waits for the extension to send
        // a fresh (signed) URL for the same row.
        public void BackendRefreshUrl(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;

            string page = it.Referer;
            Uri u;
            if (Util.IsBlank(page) || !Uri.TryCreate(page, UriKind.Absolute, out u) ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                Alert(L("msg.refresh_no_page"), L("app.title"), MessageBoxIcon.Information);
                return;
            }

            BackendPause(id);
            it.RefreshPending = true;
            it.RefreshAt = DateTime.Now;
            _downloads.Pause(it);
            it.Eta = "Waiting for browser link";
            PushState();
            SaveState();
            Log("refresh: waiting for a new link for #" + it.Id + " via " + page);
            try
            {
                Process.Start(page);
            }
            catch (Exception ex)
            {
                it.RefreshPending = false;
                Log("refresh: open page: " + ex.Message);
                Alert(ex.Message, L("app.title"), MessageBoxIcon.Warning);
                PushState();
            }
        }

        private void CheckRefreshWaits()
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                {
                    if (!it.RefreshPending) continue;
                    if ((DateTime.Now - it.RefreshAt).TotalMinutes <= 30) continue;
                    it.RefreshPending = false;
                    if (it.Eta == "Waiting for browser link") it.Eta = "Paused";
                    changed = true;
                }
            }
            if (changed) PushState();
        }

        // Restarts every failed row through the existing restart path.
        public void BackendRetryAll()
        {
            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            foreach (DownloadItem it in snap)
                if (it.State == DownloadState.Failed && !it.Merged) BackendRestart(it.Id);
            PushState();
        }

        public void BackendSetPriority(int id, int priority)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            if (priority > 1) priority = 1;
            if (priority < -1) priority = -1;
            it.Priority = priority;
            SaveState();
            PushState();
            TryStartNext();
        }

        // delta: -1 = move up, +1 = move down. Only queued rows are reordered.
        public void MoveInQueue(int id, int delta)
        {
            DownloadItem it = FindItem(id);
            if (it == null || delta == 0) return;
            if (it.State != DownloadState.Queued) return;
            if (!_downloads.Move(it, delta < 0 ? -1 : 1)) return;
            SaveState();
            PushState();
        }

        public void BackendSetItemOptions(int id, long speedKbps, string afterDone)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            if (speedKbps < 0) speedKbps = 0;
            bool speedChanged = it.SpeedKbps != speedKbps;
            it.SpeedKbps = speedKbps;

            string a = Util.IsBlank(afterDone) ? "" : afterDone.Trim().ToLowerInvariant();
            if (a != "none" && a != "open" && a != "folder" && a != "shutdown") a = "";
            it.AfterDone = a;

            // aria2 only reads the speed cap at startup; -c resumes the same file.
            if (speedChanged && (it.State == DownloadState.Downloading || it.State == DownloadState.Starting))
            {
                bool changed = false;
                if (_engine != null && !Util.IsBlank(it.EngineId))
                {
                    try
                    {
                        _engine.ChangeOption(it.EngineId, "max-download-limit",
                            speedKbps > 0 ? speedKbps + "K" : "0");
                        changed = true;
                    }
                    catch { }
                }
                if (!changed) ResumeItem(it, false);
            }

            SaveState();
            PushState();
        }

        public void BackendRemoveItem(int id, int deleteFile)
        {
            string path = null;
            DownloadItem it = FindItem(id);
            if (it != null)
            {
                it.WantRun = false;
                it.Abort = true;
                // The worker re-checks Skip when it stops, so a late engine add
                // cannot leave the file behind after the delete below races it.
                it.Skip = deleteFile != 0;
                path = it.LocalPath;
                ForgetGid(it);
            }

            _downloads.RemoveById(id);

            DownloadProgressForm f;
            lock (_popups)
            {
                if (_popups.TryGetValue(id, out f))
                {
                    _popups.Remove(id);
                    if (f != null && !f.IsDisposed) { try { f.CloseNow(); } catch { } }
                }
            }
            if (deleteFile != 0 && !Util.IsBlank(path))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log("delete file: " + ex.Message); }
                try { if (File.Exists(path + ".aria2")) File.Delete(path + ".aria2"); } catch { }
                try { if (Directory.Exists(path + ".zidseg")) Directory.Delete(path + ".zidseg", true); } catch { }
            }
            PushState();
            SaveState();
        }

        public void BackendPauseAll()
        {
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                {
                    if (DownloadStates.IsActive(it.State) ||
                        it.State == DownloadState.WaitingNetwork)
                    {
                        it.WantRun = false;
                        it.Abort   = true;
                        it.Speed   = 0;
                        it.ActiveConnections = 0;
                        it.ResumeOnLoad = false;
                        _downloads.Pause(it);
                        it.Eta     = "Paused";
                    }
                }
            }
            PushState();
            SaveState();
        }

        public void BackendResumeAll()
        {
            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            foreach (DownloadItem it in snap)
                if (it.State == DownloadState.Paused || it.State == DownloadState.Failed ||
                    it.State == DownloadState.WaitingNetwork)
                    ResumeItem(it, false);

            PushState();
        }

        // Refresh the "file moved or deleted" marker before acting on the row.
        private void MarkMissingFile(DownloadItem it)
        {
            if (Recovery.MarkMissing(it, Log)) PushState();
        }

        // Walks completed rows when the window is activated; sees files deleted
        // or restored outside ZID without a restart.
        private void CheckCompletedFiles()
        {
            if ((DateTime.Now - _filesCheckedAt).TotalSeconds < 30) return;
            _filesCheckedAt = DateTime.Now;
            bool changed = false;
            foreach (DownloadItem it in _downloads.Snapshot())
                if (Recovery.MarkMissing(it, Log)) changed = true;
            if (changed) PushState();
        }

        public void BackendOpen(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            MarkMissingFile(it);
            if (!string.IsNullOrEmpty(it.LocalPath) && File.Exists(it.LocalPath))
            {
                try { System.Diagnostics.Process.Start(it.LocalPath); return; } catch { }
            }
            Alert(L("msg.file_not_found") + (it.LocalPath ?? it.FileName),
                  L("msg.open"), MessageBoxIcon.Warning);
        }

        public void BackendOpenFolder(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            MarkMissingFile(it);
            string path = it.LocalPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    string sel = "\"" + path.Replace("\"", "\"\"") + "\"";
                    System.Diagnostics.Process.Start("explorer.exe", "/select," + sel);
                    return;
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string d = "\"" + dir.Replace("\"", "\"\"") + "\"";
                        System.Diagnostics.Process.Start("explorer.exe", d);
                        return;
                    }
                }
                catch { }
            }
            try { System.Diagnostics.Process.Start(_downloadFolder); } catch { }
        }

        public void BackendCopyUrl(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;
            try { Clipboard.SetText(it.Url); _capture.NoteSelfCopy(it.Url); } catch { }
        }

        public string BackendGetItemInfo(int id) { return BackendGetItemInfo(id, false); }

        public string BackendGetItemInfo(int id, bool includeSecrets)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return "{}";

            string folder = "";
            try
            {
                folder = string.IsNullOrEmpty(it.LocalPath)
                       ? _downloadFolder
                       : Path.GetDirectoryName(it.LocalPath);
            }
            catch { }

            return "{\"id\":" + it.Id +
                   ",\"fileName\":\"" + Aria.JsonEscape(it.FileName) + "\"" +
                   ",\"url\":\"" + Aria.JsonEscape(it.Url) + "\"" +
                   ",\"cookie\":\"" + Aria.JsonEscape(includeSecrets ? it.Cookie : Util.MaskValue(it.Cookie)) + "\"" +
                   ",\"headers\":\"" + Aria.JsonEscape(includeSecrets ? it.Headers : Util.MaskValue(it.Headers)) + "\"" +
                   ",\"checksum\":\"" + Aria.JsonEscape(it.Checksum) + "\"" +
                   ",\"mirror\":\"" + Aria.JsonEscape(it.Mirror) + "\"" +
                   ",\"proxy\":\"" + Aria.JsonEscape(includeSecrets ? it.Proxy : Util.MaskValue(it.Proxy)) + "\"" +
                   ",\"type\":\"" + Aria.JsonEscape(it.Type) + "\"" +
                   ",\"localPath\":\"" + Aria.JsonEscape(it.LocalPath) + "\"" +
                   ",\"folder\":\"" + Aria.JsonEscape(folder) + "\"" +
                   ",\"userAgent\":\"" + Aria.JsonEscape(it.UserAgent) + "\"" +
                   ",\"referer\":\"" + Aria.JsonEscape(it.Referer) + "\"" +
                   ",\"size\":" + it.Size +
                   ",\"bytesReceived\":" + it.BytesReceived +
                   ",\"speed\":" + it.Speed +
                   ",\"eta\":\"" + Aria.JsonEscape(it.Eta) + "\"" +
                   ",\"connections\":" + it.ActiveConnections +
                   ",\"retry\":" + it.RetryCount +
                   ",\"parts\":" + it.MaxConn +
                 ",\"speedKbps\":" + it.SpeedKbps +
                 ",\"afterDone\":\"" + Aria.JsonEscape(it.AfterDone) + "\"" +
                 ",\"priority\":" + it.Priority +
                 ",\"status\":\"" + Aria.JsonEscape(it.Status) + "\"" +
                   ",\"startAt\":\"" + (it.StartAt == DateTime.MinValue ? "" : it.StartAt.ToString("HH:mm")) + "\"" +
                   ",\"started\":\"" + it.Date.ToString("yyyy-MM-dd HH:mm") + "\"}";
        }

        public void BackendBrowserIntegration()
        {
            using (BrowserIntegrationForm f = new BrowserIntegrationForm(_lang, _settings.Theme))
                f.ShowDialog(this);
        }

        public void BackendAbout()
        {
            string errors = Logger.RecentErrors(10);
            string text = L("app.title") + "\n\n" +
                L("about.folder") + "\n" + _downloadFolder + "\n\n" +
                L("about.state") + "\n" + _stateFile + "\n\n" +
                L("about.config") + "\n" + _configFile + "\n\n" +
                L("about.recent_errors") + "\n" +
                (Util.IsBlank(errors) ? L("about.no_errors") : errors);
            int pick = MsgBox.Choose(this, text, L("about.title"),
                                     new string[] { L("menu.check_updates"), L("btn.close") },
                                     _lang, _settings.Theme == "dark");
            if (pick == 0) BackendCheckUpdates();
        }

        // Manual check from About; automatic checks stay silent unless there is
        // something to offer.
        public void BackendCheckUpdates()
        {
            StartUpdateCheck(true);
        }

        private void CheckAutoUpdate()
        {
            if (!_settings.UpdateCheck || _updateChecking) return;
            DateTime last = DateTime.MinValue;
            if (_settings.LastUpdateCheck > 0)
            {
                try { last = new DateTime(_settings.LastUpdateCheck, DateTimeKind.Utc); }
                catch { }
            }
            if (DateTime.UtcNow - last < TimeSpan.FromDays(_settings.UpdateDays)) return;
            StartUpdateCheck(false);
        }

        private void StartUpdateCheck(bool manual)
        {
            if (_updateChecking) return;
            _updateChecking = true;
            string local = AppVersion();
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateChecker.UpdateInfo info = null;
                string error = "";
                try { info = UpdateChecker.Check(local); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _updateChecking = false;
                        OnUpdateChecked(info, error, manual);
                    });
                }
                catch { _updateChecking = false; }
            });
        }

        private static string AppVersion()
        {
            try
            {
                Version v = new Version(Application.ProductVersion);
                string s = v.Major + "." + v.Minor;
                if (v.Build > 0) s += "." + v.Build;
                if (v.Revision > 0) s += "." + v.Revision;
                return s;
            }
            catch { return Application.ProductVersion; }
        }

        private void OnUpdateChecked(UpdateChecker.UpdateInfo info, string error, bool manual)
        {
            _settings.LastUpdateCheck = DateTime.UtcNow.Ticks;
            SaveConfig();

            if (info == null)
            {
                if (manual)
                    Alert(L("update.error").Replace("{0}", error), L("update.title"),
                          MessageBoxIcon.Warning);
                return;
            }

            if (info.IsNewer)
            {
                string text = L("update.available")
                    .Replace("{0}", info.Version)
                    .Replace("{1}", AppVersion());
                if (!Util.IsBlank(info.Notes))
                    text += "\n\n" + L("update.notes") + "\n" + info.Notes;
                if (MsgBox.Confirm(this, text, L("update.title"),
                                   L("update.download"), L("btn.cancel"),
                                   _lang, _settings.Theme == "dark") == DialogResult.OK)
                {
                    AddDownload(UpdateChecker.SetupUrl(info), info.Setup, "", "", "", "", true);
                    Alert(L("update.downloading"), L("update.title"), MessageBoxIcon.Information);
                }
                return;
            }

            if (manual)
                Alert(L("update.latest").Replace("{0}", AppVersion()),
                      L("update.title"), MessageBoxIcon.Information);
        }

        public void BackendOpenLog()
        {
            try
            {
                if (!File.Exists(Logger.Path)) Logger.Info("log opened");
                Process.Start("explorer.exe", "/select," + Logger.Path);
            }
            catch (Exception ex)
            {
                Alert(ex.Message, L("menu.open_log"), MessageBoxIcon.Warning);
            }
        }

        public void BackendCopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(BuildDiagnostics());
                Alert(L("msg.diag_copied"), L("menu.copy_diag"), MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("diagnostics copy: " + ex.Message);
                Alert(ex.Message, L("menu.copy_diag"), MessageBoxIcon.Warning);
            }
        }

        // Versions + engine + log tail. No URLs, cookies or headers.
        private string BuildDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L("app.title") + " " + Application.ProductVersion);
            sb.AppendLine(".NET " + Environment.Version);
            sb.AppendLine("OS " + Environment.OSVersion);
            sb.AppendLine("engine: " + (_engine == null ? "not started"
                : (Util.IsBlank(_engineError) ? "connected" : "error: " + _engineError)));
            sb.AppendLine("downloads: " + _downloads.Count);
            sb.AppendLine("data: " + AppPaths.DataDir + (AppPaths.Portable ? " (portable)" : ""));
            sb.AppendLine("state: " + _stateFile);
            sb.AppendLine("log: " + Logger.Path);
            sb.AppendLine();
            sb.Append(Logger.Tail(200));
            return sb.ToString();
        }

        // -----------------------------------------------------------------
        //  Progress popup
        // -----------------------------------------------------------------
        public void BackendShowProgress(int id)
        {
            DownloadItem it = FindItem(id);
            if (it == null) return;

            DownloadProgressForm f;
            lock (_popups)
            {
                if (!_popups.TryGetValue(id, out f) || f == null || f.IsDisposed)
                {
                    f = new DownloadProgressForm(it, this, _settings.PartsPerDownload, _settings.Theme, _lang);
                    _popups[id] = f;
                }
            }
            if (f.Visible) { f.Activate(); return; }
            f.Show();
            f.Activate();
        }

        public void BackendHideProgress(int id)
        {
            DownloadProgressForm f;
            lock (_popups)
            {
                if (_popups.TryGetValue(id, out f) && f != null && !f.IsDisposed)
                    f.Hide();
            }
        }

        internal void ForgetPopup(int id)
        {
            lock (_popups)
            {
                DownloadProgressForm f;
                if (_popups.TryGetValue(id, out f)) _popups.Remove(id);
            }
        }

        // A refresh waits for the next request from the same page. Exact page match
        // (query included) keeps YouTube watch pages apart.
        private static bool SamePage(string a, string b)
        {
            if (Util.IsBlank(a) || Util.IsBlank(b)) return false;
            try
            {
                Uri ua = new Uri(a), ub = new Uri(b);
                return string.Equals(ua.GetLeftPart(UriPartial.Query),
                                     ub.GetLeftPart(UriPartial.Query),
                                     StringComparison.Ordinal);
            }
            catch { return string.Equals(a, b, StringComparison.Ordinal); }
        }

        private static bool KindFits(string a, string b)
        {
            a = (a ?? "").ToLowerInvariant();
            b = (b ?? "").ToLowerInvariant();
            if (a == b) return true;
            return (a == "video" || a == "muxed") && (b == "video" || b == "muxed");
        }

        private DownloadItem TakeRefreshMatch(NeatRequest req)
        {
            DownloadItem best = null;
            int bestScore = -1;
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                {
                    if (!it.RefreshPending) continue;
                    if ((DateTime.Now - it.RefreshAt).TotalMinutes > 30) continue;
                    if (!KindFits(it.Kind, req.Kind)) continue;
                    if (!SamePage(it.Referer, req.Referer)) continue;

                    int score = 1;
                    if (string.Equals((it.Kind ?? "").ToLowerInvariant(),
                                      (req.Kind ?? "").ToLowerInvariant(),
                                      StringComparison.Ordinal))
                        score += 4;
                    if (!Util.IsBlank(it.FileName) && !Util.IsBlank(req.FileName) &&
                        string.Equals(Path.GetFileName(it.FileName),
                                      Path.GetFileName(req.FileName),
                                      StringComparison.OrdinalIgnoreCase))
                        score += 8;
                    if (it.Size > 0 && req.Size > 0 && it.Size == req.Size) score += 2;
                    if (score > bestScore) { best = it; bestScore = score; }
                }
            }
            return best;
        }

        private void ApplyRefresh(DownloadItem it, NeatRequest req)
        {
            it.RefreshPending = false;
            it.Url = req.Url;
            if (!Util.IsBlank(req.Referer)) it.Referer = req.Referer;
            if (!Util.IsBlank(req.Cookie)) it.Cookie = req.Cookie;
            if (!Util.IsBlank(req.Headers)) it.Headers = Util.NormalizeHeaders(req.Headers);
            if (!Util.IsBlank(req.UserAgent)) it.UserAgent = req.UserAgent;
            if (req.Size > 0) it.Size = req.Size;
            if (!Util.IsBlank(req.Kind)) it.Kind = req.Kind.ToLowerInvariant();
            // The new click carries a fresh pair token; without it the refreshed
            // video would not find the audio sent with the same click.
            if (!Util.IsBlank(req.Pair)) it.MergeKey = req.Pair.Trim();
            _downloads.Queue(it);
            it.Eta = "Connecting...";
            it.WantRun = true;
            it.ResumeOnLoad = false;
            ResumeItem(it, false);
            PushState();
            SaveState();
            Log("refresh: new link for #" + it.Id + " (" + req.Kind + ")");
        }

        // True when the request was accepted. The extension cancels its own
        // browser copy only on a true reply, so an automatic hand-off must
        // finish queueing before we answer; a manual one only opens a dialog.
        private bool OnNeatDownload(NeatRequest req)
        {
            if (req == null) return false;
            // Before the window handle exists InvokeRequired is false, so a
            // request must be refused instead of touching the UI from here.
            if (!IsHandleCreated) return false;
            if (InvokeRequired)
            {
                if (req.Auto)
                {
                    try { return (bool)Invoke(new NeatDownloadHandler(OnNeatDownload), new object[] { req }); }
                    catch { Log("neat: automatic request dropped while the window was closing"); return false; }
                }
                try { BeginInvoke(new NeatDownloadHandler(OnNeatDownload), new object[] { req }); }
                catch { Log("neat: request dropped while the window was closing"); return false; }
                return true;
            }
            // Logged after the UI hop, or every request would appear twice.
            string host = "";
            try { host = new Uri(req.Url).Host; } catch { }
            Log("neat: kind=" + req.Kind + " auto=" + req.Auto + " cookie=" +
                (req.Cookie.Length > 0 ? "yes" : "no") + " host=" + host);
            // A request from the extension must not stay behind the browser.
            RestoreFromTray();
            DownloadItem refreshItem = TakeRefreshMatch(req);
            if (refreshItem != null)
            {
                ApplyRefresh(refreshItem, req);
                return true;
            }
            // A blocked website stays in the browser: no dialog, no takeover.
            if (Util.IsBlockedHost(_settings.BlockedHosts, host))
            {
                Log("neat: blocked host " + host);
                return false;
            }
            if (req.Auto)
            {
                // A URL the app cannot queue must leave the browser's copy alone.
                if (!AddService.IsSupportedUrl(req.Url)) return false;
                try
                {
                    AddDownload(req.Url, req.FileName, "", req.Cookie, "", "", true,
                                req.Referer, req.UserAgent, req.Kind, true, req.Pair, req.Headers, "", "", "");
                    return true;
                }
                catch (Exception ex)
                {
                    Log("neat: automatic add failed: " + ex.Message);
                    return false;
                }
            }
            using (NeatDownloadForm f = new NeatDownloadForm(req, _downloadFolder, _settings.AutoSort, _lang, _settings.Theme))
            {
                DialogResult r = f.ShowDialog(this);
                if (f.BlockDomain)
                {
                    // The user asked for this domain to stay in the browser, so
                    // this download is not added and the extension keeps its copy.
                    _settings.BlockedHosts = Util.AddBlockedHost(_settings.BlockedHosts, host);
                    SaveConfig();
                    PushState();
                    Log("neat: blocked host " + host);
                    return false;
                }
                if (r != DialogResult.OK) return true;
                AddDownload(req.Url, f.FileName, f.Folder, req.Cookie, "", "", f.StartNow,
                            req.Referer, req.UserAgent, req.Kind, false, req.Pair, req.Headers, "", "", "");
            }
            return true;
        }

        // -----------------------------------------------------------------
        //  Extras: clipboard watch, shutdown-when-done, taskbar progress, export
        // -----------------------------------------------------------------
        internal void OnClipboardChanged()
        {
            if (!_settings.ClipboardWatch) return;
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return;
                text = Clipboard.GetText().Trim();
            }
            catch { return; }
            if (!_capture.Accept(text, Environment.TickCount)) return;
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                    if (it.Url == text) return;
            }
            BackendAdd(text, "", "", "", "", "");
        }

        private void ApplyClipWatch()
        {
            if (_settings.ClipboardWatch && _clipWatcher == null)
            {
                try
                {
                    ClipWatcher w = new ClipWatcher(this);
                    w.AssignHandle(this.Handle);
                    w.Hook();
                    _clipWatcher = w;
                }
                catch (Exception ex) { _clipWatcher = null; Log("clipboard watch: " + ex.Message); }
            }
            else if (!_settings.ClipboardWatch && _clipWatcher != null)
            {
                try { _clipWatcher.Unhook(); } catch { }
                _clipWatcher = null;
            }
        }

        // One-shot "start queue at HH:MM": clears once the time is reached.
        // Daily mode repeats the window; a stop time pauses active downloads.
        private void CheckSchedule()
        {
            int now = SchedulePolicy.SecondsOfDay(DateTime.Now);

            if (_settings.ScheduleSecs >= 0 && !SchedulePending())
            {
                if (!_settings.ScheduleDaily)
                {
                    _settings.ScheduleSecs = -1;
                    _settings.ScheduleAt   = "";
                    SaveConfig();
                }
                TryStartNext();
                if (_settings.ScheduleDaily) ResumeScheduled();
            }

            if (_settings.StopSecs >= 0)
            {
                bool inStop = StopReached(now);
                if (inStop && !_stopFired)
                {
                    _stopFired = true;
                    PauseForSchedule();
                    if (!_settings.ScheduleDaily)
                    {
                        _settings.StopSecs = -1;
                        _settings.StopAt   = "";
                        SaveConfig();
                    }
                }
                else if (!inStop) _stopFired = false;
            }

            ApplySpeedSchedule(now);
            // A delayed row may become ready without any other event.
            TryStartNext();
        }

        // The window cap is a temporary limit; per-item caps still win.
        private long EffectiveSpeedKbps()
        {
            if (_settings.SpeedLimitKbps > 0 &&
                _settings.SpeedLimitFromSecs >= 0 && _settings.SpeedLimitToSecs >= 0 &&
                SchedulePolicy.InWindow(_settings.SpeedLimitFromSecs, _settings.SpeedLimitToSecs,
                                        SchedulePolicy.SecondsOfDay(DateTime.Now)))
                return _settings.SpeedLimitKbps;
            return _settings.MaxSpeedKbps;
        }

        // Apply or remove the window cap on active downloads without restarting them.
        private void ApplySpeedSchedule(int now)
        {
            bool active = _settings.SpeedLimitKbps > 0 &&
                          _settings.SpeedLimitFromSecs >= 0 && _settings.SpeedLimitToSecs >= 0 &&
                          SchedulePolicy.InWindow(_settings.SpeedLimitFromSecs,
                                                  _settings.SpeedLimitToSecs, now);
            if (active == _speedWindowApplied) return;
            _speedWindowApplied = active;

            long cap = active ? _settings.SpeedLimitKbps : _settings.MaxSpeedKbps;
            IDownloadEngine engine = _engine;
            if (engine == null) return;
            string value = cap > 0 ? cap.ToString(CultureInfo.InvariantCulture) + "K" : "0";
            foreach (DownloadItem it in _downloads.Snapshot())
            {
                if (it.SpeedKbps > 0) continue;
                if (it.State != DownloadState.Downloading && it.State != DownloadState.Starting) continue;
                if (Util.IsBlank(it.EngineId)) continue;
                try { engine.ChangeOption(it.EngineId, "max-download-limit", value); }
                catch { }
            }
        }

        // Stop time: remember which rows the scheduler paused so a daily
        // window can resume them, unlike a manual pause.
        private void PauseForSchedule()
        {
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                    if (DownloadStates.IsActive(it.State))
                        it.SchedPaused = true;
            }
            BackendPauseAll();
        }

        private void ResumeScheduled()
        {
            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            foreach (DownloadItem it in snap)
            {
                if (!it.SchedPaused) continue;
                it.SchedPaused = false;
                if (it.State == DownloadState.Paused) ResumeItem(it, false);
            }
        }

        // Per-item "when finished" actions (dialog, open file/folder, shut down).
        private void CheckAfterDone()
        {
            bool any = false;
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                    if (!it.AfterDoneRan && it.State == DownloadState.Completed && !it.Auto) { any = true; break; }
            }
            if (!any) return;

            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            foreach (DownloadItem it in snap)
            {
                if (it.AfterDoneRan || it.State != DownloadState.Completed) continue;
                if (it.Auto) continue;

                it.AfterDoneRan = true;
                if (it.FinishedAt == default(DateTime)) it.FinishedAt = DateTime.Now;

                // Notify from the tray when the window is not on screen.
                if (it.AfterDone != "shutdown" && _tray != null &&
                    (!Visible || WindowState == FormWindowState.Minimized))
                {
                    _tray.BalloonTipTitle = L("app.title");
                    _tray.BalloonTipText  = L("tray.done") + " " + it.FileName;
                    _tray.ShowBalloonTip(3000);
                }

                if (it.AfterDone == "none") continue;

                try
                {
                    if (it.AfterDone == "open")
                    {
                        if (!Util.IsBlank(it.LocalPath) && File.Exists(it.LocalPath))
                            Process.Start(it.LocalPath);
                    }
                    else if (it.AfterDone == "folder")
                    {
                        BackendOpenFolder(it.Id);
                    }
                    else if (it.AfterDone == "shutdown")
                    {
                        _shutdownStarted = true;
                        Log("download finished, shutting down");
                        Process.Start("shutdown", "-s -t 60");
                    }
                    else
                    {
                        // The summary replaces the detail popup: a finished item must not
                        // leave a window that still offers Pause/Resume and options.
                        DownloadProgressForm pf;
                        lock (_popups)
                        {
                            if (_popups.TryGetValue(it.Id, out pf) && pf != null && !pf.IsDisposed)
                                pf.CloseNow();
                        }
                        new FinishDownloadForm(it, _lang, _settings.Theme).Show(this);
                    }
                }
                catch (Exception ex) { Log("after done: " + ex.Message); }
            }
        }

        private void MaybeShutdown()
        {
            if (!_settings.ShutdownWhenDone || _shutdownStarted) return;
            if (_queue.ActiveCount > 0 || HasQueued()) return;
            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            if (snap.Count == 0) return;
            foreach (DownloadItem it in snap)
                if (it.State != DownloadState.Completed && it.State != DownloadState.Failed) return;

            _shutdownStarted = true;
            Log("queue finished, shutting down");
            try { Process.Start("shutdown", "-s -t 60"); }
            catch (Exception ex) { Log("shutdown: " + ex.Message); }
        }

        private int UpdateTaskbar()
        {
            int active = 0, paused = 0, failed = 0, sum = 0, pausedSum = 0;
            lock (_lock)
            {
                foreach (DownloadItem it in _downloads.Snapshot())
                {
                    switch (it.State)
                    {
                        case DownloadState.Downloading:
                        case DownloadState.Starting:
                        case DownloadState.Queued:
                            active++;
                            sum += it.Progress;
                            break;
                        case DownloadState.Paused:
                        case DownloadState.WaitingNetwork:
                            paused++;
                            pausedSum += it.Progress;
                            break;
                        case DownloadState.Failed:
                            failed++;
                            break;
                    }
                }
            }

            // Keep the machine awake while bytes are moving.
            bool block = active > 0;
            if (block != _sleepBlocked)
            {
                _sleepBlocked = block;
                try { SetThreadExecutionState(ES_CONTINUOUS | (block ? ES_SYSTEM_REQUIRED : 0)); }
                catch { }
            }

            if (Environment.OSVersion.Version.Major >= 6)
            {
                // 2 normal, 3 error, 4 paused, 0 clear (completed/empty).
                int state = 0, pct = -1;
                if (active > 0)      { state = 2; pct = sum / active; }
                else if (paused > 0) { state = 4; pct = pausedSum / paused; }
                else if (failed > 0) { state = 3; pct = 100; }

                if (state != _lastTaskbarState || pct != _lastTaskbarPct)
                {
                    _lastTaskbarState = state;
                    _lastTaskbarPct = pct;
                    TaskbarProgress.Set(this.Handle, state, pct);
                }
            }
            return active;
        }

        // Reads a .txt file for the Add dialog (one URL per line).
        public string BackendImportUrls()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                d.Multiselect = false;
                if (d.ShowDialog(this) != DialogResult.OK) return "";
                try
                {
                    FileInfo fi = new FileInfo(d.FileName);
                    if (fi.Length > 1048576) return "";
                    return File.ReadAllText(d.FileName);
                }
                catch (Exception ex) { Log("import urls: " + ex.Message); return ""; }
            }
        }

        public void BackendExportList()
        {
            List<DownloadItem> snap;
            snap = _downloads.Snapshot();
            try
            {
                SaveFileDialog d = new SaveFileDialog();
                d.Filter   = "CSV (*.csv)|*.csv|All files (*.*)|*.*";
                d.FileName = "downloads.csv";
                if (d.ShowDialog() != DialogResult.OK) return;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("id,fileName,status,type,size,progress,url,localPath,date");
                foreach (DownloadItem it in snap)
                {
                    sb.Append(it.Id).Append(',')
                      .Append(Util.Csv(it.FileName)).Append(',')
                      .Append(Util.Csv(it.Status)).Append(',')
                      .Append(Util.Csv(it.Type)).Append(',')
                      .Append(it.Size).Append(',')
                      .Append(it.Progress).Append(',')
                      .Append(Util.Csv(it.Url)).Append(',')
                      .Append(Util.Csv(it.LocalPath)).Append(',')
                      .Append(Util.Csv(it.Date.ToString("yyyy-MM-dd HH:mm"))).AppendLine();
                }
                File.WriteAllText(d.FileName, sb.ToString());
            }
            catch (Exception ex)
            {
                Log("export: " + ex.Message);
                Alert(L("msg.export_failed") + " " + ex.Message, L("msg.export"),
                      MessageBoxIcon.Error);
            }
        }

        // -----------------------------------------------------------------
        //  Helpers
        // -----------------------------------------------------------------
        private DownloadItem FindItem(int id)
        {
            return _downloads.Find(id);
        }

    }
}
