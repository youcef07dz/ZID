using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZID
{
    // =====================================================================
    //  Detached per-download progress window
    // =====================================================================
    internal sealed class DownloadProgressForm : Form
    {
        private readonly DownloadItem _item;
        private readonly MainForm _owner;
        private readonly int _defaultParts;
        private readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer();
        private readonly bool _dark;
        private readonly Dictionary<string, string> _lang;

        private Label  _lblName, _lblUrl, _lblType, _lblStarted, _lblStatus;
        private Label  _lblSize, _lblReceived, _lblSpeed, _lblEta, _lblPath;
        private Label  _lblPartsSummary, _lblSaved;
        private SlimBar _bar;
        private Panel  _foot, _pageDownload, _pageOptions;
        private Button _btnDownload, _btnOptions;
        private Button _btnHide, _btnClose, _btnOpenFolder, _btnPauseResume, _btnApply;
        private TextField _txtSpeed;
        private ComboBox _cmbAfter;

        private bool _allowClose;
        private NotifyIcon _tray;
        private Icon _trayIcon;

        // Ongoing downloads carry their own icon so the tray does not look like the app.
        private static Icon LoadIcon(string name)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return null;
        }

        public DownloadProgressForm(DownloadItem item, MainForm owner, int defaultParts,
                                    string theme, Dictionary<string, string> lang)
        {
            _item  = item;
            _owner = owner;
            _defaultParts = defaultParts > 0 ? defaultParts : 1;
            _dark  = (theme == "dark");
            _lang  = lang ?? new Dictionary<string, string>();

            Icon downloadIcon = LoadIcon("download.ico");
            if (downloadIcon != null) Icon = downloadIcon;

            Text            = Lang.Get(_lang, "popup.title", "Download - {name}")
                                  .Replace("{name}", item.FileName);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox     = true;
            MaximizeBox     = false;
            ShowInTaskbar   = true;
            ClientSize      = new Size(560, 430);

            Theme.Prepare(this, _dark);
            BuildUi();
            LoadOptions();

            // Esc hides the popup like the close box does.
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && !_cmbAfter.DroppedDown) { HideToTray(); e.Handled = true; }
            };

            _refresh.Interval = 1000;
            _refresh.Tick += delegate { if (Visible) RefreshUi(); };
            _refresh.Start();
            RefreshUi();

            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                // The ×, Esc and Alt+F4 all arrive as a close attempt: hide to
                // the tray so the download keeps running and stays reachable.
                // Only CloseNow() (item removed / finished) or the owner going
                // away really disposes this window.
                if (_allowClose || e.CloseReason == CloseReason.FormOwnerClosing ||
                    e.CloseReason == CloseReason.ApplicationExitCall ||
                    e.CloseReason == CloseReason.WindowsShutDown) return;
                e.Cancel = true;
                HideToTray();
            };
            FormClosed += delegate
            {
                _refresh.Stop();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }
                if (_owner != null) _owner.ForgetPopup(_item.Id);
            };
        }

        private void BuildUi()
        {
            Panel head = Theme.Header(this, _lang, _dark);
            Button tray = Theme.ChromeButton("\u25be", _dark, Color.FromArgb(45, 58, 84));
            // Sits left of the minimize and close boxes drawn by Theme.Header.
            tray.SetBounds(head.Width - 100, 6, 28, 28);
            tray.Click += delegate { HideToTray(); };
            head.Controls.Add(tray);
            _foot = Theme.Footer(this, _dark);

            _btnDownload = Theme.Button(Lang.Get(_lang, "popup.tab_download", "Download"), true, _dark);
            _btnDownload.SetBounds(14, 50, 112, 30);
            _btnDownload.Click += delegate { SelectTab(false); };

            _btnOptions = Theme.Button(Lang.Get(_lang, "popup.tab_options", "Options"), false, _dark);
            _btnOptions.SetBounds(134, 50, 112, 30);
            _btnOptions.Click += delegate { SelectTab(true); };

            _pageDownload = new Panel();
            _pageDownload.SetBounds(0, 92, 560, 266);
            _pageDownload.BackColor = Theme.Window(_dark);

            int x = 30, y = 12;
            _lblName    = MakeLabel(x, y, 500, true);  y += 26;
            _lblUrl     = MakeLabel(x, y, 500, false); y += 20;
            _lblType    = MakeLabel(x, y, 500, false); y += 20;
            _lblStarted = MakeLabel(x, y, 500, false); y += 20;
            _lblStatus  = MakeLabel(x, y, 500, false); y += 26;

            // Progress bar runs along the bottom of the window, above the footer.
            _bar = new SlimBar();
            _bar.Dark = _dark;
            _bar.SetBounds(14, 350, 532, 18);
            Controls.Add(_bar);
            y += 22;

            _lblSize     = MakeLabel(x, y, 250, false);
            _lblReceived = MakeLabel(x + 260, y, 240, false); y += 20;
            _lblSpeed    = MakeLabel(x, y, 250, false);
            _lblEta      = MakeLabel(x + 260, y, 240, false); y += 20;
            _lblPath     = MakeLabel(x, y, 500, false); y += 26;

            _lblPartsSummary = Theme.Body("", _dark);
            _lblPartsSummary.AutoEllipsis = true;
            _lblPartsSummary.SetBounds(x, y, 500, 20);

            _pageDownload.Controls.Add(_lblName);
            _pageDownload.Controls.Add(_lblUrl);
            _pageDownload.Controls.Add(_lblType);
            _pageDownload.Controls.Add(_lblStarted);
            _pageDownload.Controls.Add(_lblStatus);
            _pageDownload.Controls.Add(_lblSize);
            _pageDownload.Controls.Add(_lblReceived);
            _pageDownload.Controls.Add(_lblSpeed);
            _pageDownload.Controls.Add(_lblEta);
            _pageDownload.Controls.Add(_lblPath);
            _pageDownload.Controls.Add(_lblPartsSummary);
            // Card added last so it sits behind the labels (first child = topmost).
            _pageDownload.Controls.Add(Theme.Card(14, 0, 532, 252, _dark));

            _pageOptions = new Panel();
            _pageOptions.SetBounds(0, 92, 560, 266);
            _pageOptions.BackColor = Theme.Window(_dark);
            _pageOptions.Visible = false;

            Label lSpeed = Theme.Section(
                Lang.Get(_lang, "label.speed_limit", "Speed limit KB/s (0 = use default)"), _dark);
            lSpeed.SetBounds(30, 16, 400, 18);

            _txtSpeed = Theme.TextField(30, 36, 150, _dark);

            Label lAfter = Theme.Section(
                Lang.Get(_lang, "popup.after", "When download finishes"), _dark);
            lAfter.SetBounds(30, 88, 400, 18);

            _cmbAfter = new ComboBox();
            _cmbAfter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbAfter.FlatStyle = FlatStyle.Flat;
            _cmbAfter.BackColor = Theme.Field(_dark);
            _cmbAfter.ForeColor = Theme.Text(_dark);
            _cmbAfter.SetBounds(30, 108, 260, 26);
            _cmbAfter.Items.Add(Lang.Get(_lang, "popup.after_dialog", "Show finished dialog"));
            _cmbAfter.Items.Add(Lang.Get(_lang, "popup.after_none", "Do nothing"));
            _cmbAfter.Items.Add(Lang.Get(_lang, "popup.after_open", "Open the file"));
            _cmbAfter.Items.Add(Lang.Get(_lang, "popup.after_folder", "Open the folder"));
            _cmbAfter.Items.Add(Lang.Get(_lang, "popup.after_shutdown", "Shut down the PC"));

            _btnApply = Theme.Button(Lang.Get(_lang, "btn.apply", "Apply"), true, _dark);
            _btnApply.SetBounds(30, 156, 110, 32);
            _btnApply.Click += delegate { ApplyOptions(); };

            _lblSaved = Theme.MutedLabel("", _dark);
            _lblSaved.BackColor = Theme.Panel(_dark);  // sits on the card, not the page
            _lblSaved.SetBounds(150, 163, 240, 18);

            _pageOptions.Controls.Add(lSpeed);
            _pageOptions.Controls.Add(_txtSpeed);
            _pageOptions.Controls.Add(lAfter);
            _pageOptions.Controls.Add(_cmbAfter);
            _pageOptions.Controls.Add(_btnApply);
            _pageOptions.Controls.Add(_lblSaved);
            // Card added last so it sits behind the fields (first child = topmost).
            _pageOptions.Controls.Add(Theme.Card(14, 0, 532, 252, _dark));

            _btnPauseResume = Theme.Button(Lang.Get(_lang, "btn.pause", "Pause"), true, _dark);
            _btnPauseResume.SetBounds(14, 13, 96, 32);
            _btnPauseResume.Click += delegate
            {
                if (DownloadStates.IsActive(_item.State))
                    _owner.BackendPause(_item.Id);
                else
                    _owner.BackendResume(_item.Id);
                RefreshUi();
            };

            _btnOpenFolder = Theme.Button(Lang.Get(_lang, "btn.open_folder", "Open folder"), false, _dark);
            _btnOpenFolder.SetBounds(118, 13, 110, 32);
            _btnOpenFolder.Click += delegate
            {
                try
                {
                    if (!string.IsNullOrEmpty(_item.LocalPath) && File.Exists(_item.LocalPath))
                    {
                        string sel = "\"" + _item.LocalPath.Replace("\"", "\"\"") + "\"";
                        System.Diagnostics.Process.Start("explorer.exe", "/select," + sel);
                    }
                    else if (!string.IsNullOrEmpty(_item.LocalPath))
                    {
                        string dir = Path.GetDirectoryName(_item.LocalPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            string d = "\"" + dir.Replace("\"", "\"\"") + "\"";
                            System.Diagnostics.Process.Start("explorer.exe", d);
                        }
                    }
                }
                catch { }
            };

            _btnHide = Theme.Button(Lang.Get(_lang, "btn.hide", "Hide"), false, _dark);
            _btnHide.SetBounds(344, 13, 90, 32);
            _btnHide.Click += delegate { HideToTray(); };

            _btnClose = Theme.Button(Lang.Get(_lang, "btn.close", "Close"), false, _dark);
            _btnClose.SetBounds(442, 13, 100, 32);
            _btnClose.Click += delegate { _allowClose = true; Close(); };

            Controls.Add(_btnDownload);
            Controls.Add(_btnOptions);
            Controls.Add(_pageDownload);
            Controls.Add(_pageOptions);
            _foot.Controls.Add(_btnPauseResume);
            _foot.Controls.Add(_btnOpenFolder);
            _foot.Controls.Add(_btnHide);
            _foot.Controls.Add(_btnClose);
            // Tabs and options come first in the tab order, footer actions last.
            _foot.TabIndex = Controls.Count;
        }

        // A hidden window keeps no taskbar button, so the tray icon is the way back.
        private void HideToTray()
        {
            if (_tray == null)
            {
                _tray = new NotifyIcon();
                _trayIcon = LoadIcon("download.ico");
                if (_trayIcon == null) _trayIcon = LoadIcon("app.ico");
                _tray.Icon = _trayIcon != null ? _trayIcon : SystemIcons.Application;
                string tip = _item.FileName ?? "";
                _tray.Text = tip.Length > 60 ? tip.Substring(0, 60) : tip;
                _tray.Click += delegate { RestoreFromTray(); };
            }
            _tray.Visible = true;
            Hide();
        }

        // Shown again from the main window: the tray icon is no longer needed.
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _tray != null) _tray.Visible = false;
        }

        private void RestoreFromTray()
        {
            if (_tray != null) _tray.Visible = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // Programmatic close (item removed, download finished): bypass the
        // hide-to-tray rule so the window really goes away.
        internal void CloseNow()
        {
            _allowClose = true;
            try { Close(); } catch { }
        }

        private void LoadOptions()
        {
            _txtSpeed.Text = _item.SpeedKbps > 0 ? _item.SpeedKbps.ToString() : "0";
            string a = _item.AfterDone ?? "";
            if (a == "none") _cmbAfter.SelectedIndex = 1;
            else if (a == "open") _cmbAfter.SelectedIndex = 2;
            else if (a == "folder") _cmbAfter.SelectedIndex = 3;
            else if (a == "shutdown") _cmbAfter.SelectedIndex = 4;
            else _cmbAfter.SelectedIndex = 0;
        }

        private void ApplyOptions()
        {
            long kbps;
            if (!long.TryParse(_txtSpeed.Text.Trim(), out kbps) || kbps < 0) kbps = 0;
            string after = "";
            switch (_cmbAfter.SelectedIndex)
            {
                case 1: after = "none"; break;
                case 2: after = "open"; break;
                case 3: after = "folder"; break;
                case 4: after = "shutdown"; break;
            }
            _owner.BackendSetItemOptions(_item.Id, kbps, after);
            _lblSaved.Text = Lang.Get(_lang, "popup.saved", "Saved");
        }

        private void SelectTab(bool options)
        {
            _pageDownload.Visible = !options;
            _pageOptions.Visible = options;
            StyleTab(_btnDownload, !options);
            StyleTab(_btnOptions, options);
        }

        private void StyleTab(Button b, bool active)
        {
            RoundButton rb = b as RoundButton;
            if (active)
            {
                b.BackColor = Theme.Accent;
                b.ForeColor = Color.White;
                if (rb != null)
                {
                    rb.BackColor2 = Theme.Accent2;
                    rb.BorderColor = Theme.AccentBorder;
                    rb.HoverBack = Theme.AccentHover;
                    rb.HoverBack2 = Theme.AccentHover2;
                    rb.DownBack = Theme.AccentBorder;
                    rb.DownBack2 = Theme.Accent;
                }
            }
            else
            {
                b.ForeColor = Color.White;
                if (rb != null) Theme.ApplySecondary(rb, _dark);
            }
        }

        private Label MakeLabel(int x, int y, int w, bool bold)
        {
            Label l = bold ? Theme.Body("", _dark) : Theme.MutedLabel("", _dark);
            l.BackColor = Theme.Panel(_dark);  // card colour: transparent labels paint the page
            l.AutoEllipsis = true;
            l.SetBounds(x, y, w, 18);
            if (bold) l.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            return l;
        }

        private void RefreshUi()
        {
            try
            {
                Text = Lang.Get(_lang, "popup.title", "Download - {name}")
                           .Replace("{name}", _item.FileName);
                _lblName.Text    = Lang.Get(_lang, "label.file", "File:") + " " + _item.FileName;
                _lblUrl.Text     = Lang.Get(_lang, "label.url", "URL:") + " " + _item.Url;
                _lblType.Text    = Lang.Get(_lang, "label.type", "Type:") + " " + (_item.Type ?? "-");
                _lblStarted.Text = Lang.Get(_lang, "label.started", "Started:") + " " + _item.Date.ToString("yyyy-MM-dd HH:mm");
                _lblStatus.Text  = Lang.Get(_lang, "label.status", "Status:") + " " + Lang.Status(_lang, _item.Status);
                _lblStatus.ForeColor = StatusColor(_item.Status);

                _bar.Value = Math.Max(0, Math.Min(100, _item.Progress));

                _lblSize.Text     = Lang.Get(_lang, "label.size", "Size:") + " " + FmtSize(_item.Size);
                _lblReceived.Text = Lang.Get(_lang, "label.received", "Received:") + " " + FmtSize(_item.BytesReceived);
                _lblSpeed.Text    = Lang.Get(_lang, "label.speed", "Speed:") + " " + (_item.Speed > 0 ? FmtSize(_item.Speed) + "/s" : "-");
                _lblEta.Text      = Lang.Get(_lang, "label.eta", "ETA:") + " " + Lang.Status(_lang, _item.Eta ?? "-");
                _lblPath.Text     = Lang.Get(_lang, "label.save_to", "Save to:") + " " + (_item.LocalPath ?? "-");

                int parts = _item.MaxConn > 0 ? _item.MaxConn : _defaultParts;
                if (parts < 1) parts = 1;
                int active = Math.Max(0, Math.Min(_item.ActiveConnections, parts));
                _lblPartsSummary.Text = Lang.Get(_lang, "popup.parts_downloading", "Parts downloading:")
                    + " " + active + " / " + parts;

                _btnPauseResume.Text =
                    DownloadStates.IsActive(_item.State)
                        ? Lang.Get(_lang, "btn.pause", "Pause")
                        : Lang.Get(_lang, "btn.resume", "Resume");
            }
            catch { }
        }

        private Color StatusColor(string status)
        {
            DownloadState state = DownloadStates.Parse(status);
            if (state == DownloadState.Completed) return Theme.Ok;
            if (state == DownloadState.Failed) return Theme.Danger;
            if (state == DownloadState.Paused ||
                state == DownloadState.WaitingNetwork) return Theme.Warn;
            if (state == DownloadState.Downloading || state == DownloadState.Starting)
                return Theme.AccentHover;
            return Theme.Muted(_dark);
        }

        private static string FmtSize(long b)
        {
            if (b <= 0) return "-";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.0") + " " + u[i];
        }
    }

    // =====================================================================
    //  "Download finished" summary window (default after-download action)
    // =====================================================================
    internal sealed class FinishDownloadForm : Form
    {
        private readonly DownloadItem _item;
        private readonly bool _dark;

        public FinishDownloadForm(DownloadItem item, Dictionary<string, string> lang, string theme)
        {
            _item = item;
            _dark = (theme == "dark");
            Dictionary<string, string> l = lang ?? new Dictionary<string, string>();

            Text            = Lang.Get(l, "popup.finished_title", "Download complete");
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ShowInTaskbar   = true;
            ClientSize      = new Size(540, 360);

            Theme.Prepare(this, _dark);
            // No CancelButton here: Esc dismisses the borderless summary window.
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            };
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            Theme.Header(this, l, _dark);
            Panel foot = Theme.Footer(this, _dark);

            Panel card = Theme.Card(14, Theme.HeaderHeight + 10, 512, 238, _dark);
            Controls.Add(card);

            Panel tile = new Panel();
            tile.SetBounds(20, 18, 42, 42);
            tile.BackColor = Theme.Panel(_dark);
            tile.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, 41, 41);
                using (GraphicsPath p = Theme.Rounded(r, 12))
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                               new Rectangle(0, 0, 41, 41), Color.FromArgb(52, 211, 153),
                               Color.FromArgb(16, 185, 129), LinearGradientMode.Vertical))
                        e.Graphics.FillPath(b, p);
                    using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255)))
                        e.Graphics.DrawPath(edge, p);
                }
                using (Pen check = new Pen(Color.White, 2.4f))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(check, new Point[]
                    {
                        new Point(12, 21), new Point(18, 27), new Point(29, 14)
                    });
                }
            };
            card.Controls.Add(tile);

            Label title = Theme.Body(Lang.Get(l, "popup.finished", "Download finished"), _dark);
            title.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            title.SetBounds(76, 24, 420, 24);
            card.Controls.Add(title);

            TimeSpan span = item.FinishedAt > item.Date ? item.FinishedAt - item.Date : TimeSpan.Zero;
            double secs = span.TotalSeconds;
            long avg = secs > 0 ? (long)(item.Size / secs) : 0;

            int y = 84;
            card.Controls.Add(Row(Lang.Get(l, "label.file", "File:") + " " + item.FileName, y, l)); y += 28;
            card.Controls.Add(Row(Lang.Get(l, "label.save_to", "Save to:") + " " + (item.LocalPath ?? "-"), y, l)); y += 28;
            card.Controls.Add(Row(Lang.Get(l, "label.size", "Size:") + " " + FmtSize(item.Size), y, l)); y += 28;
            card.Controls.Add(Row(Lang.Get(l, "label.avg_speed", "Average speed:") + " " + (avg > 0 ? FmtSize(avg) + "/s" : "-"), y, l)); y += 28;
            card.Controls.Add(Row(Lang.Get(l, "label.time_taken", "Time taken:") + " " + FmtTime(span), y, l));

            Button openFile = Theme.Button(Lang.Get(l, "btn.open_file", "Open file"), true, _dark);
            openFile.SetBounds(280, 13, 110, 32);
            openFile.Click += delegate
            {
                try
                {
                    if (!string.IsNullOrEmpty(_item.LocalPath) && File.Exists(_item.LocalPath))
                        Process.Start(_item.LocalPath);
                }
                catch { }
                Close();
            };

            Button openFolder = Theme.Button(Lang.Get(l, "btn.open_folder", "Open folder"), false, _dark);
            openFolder.SetBounds(398, 13, 124, 32);
            openFolder.Click += delegate
            {
                try
                {
                    string dir = string.IsNullOrEmpty(_item.LocalPath)
                        ? "" : Path.GetDirectoryName(_item.LocalPath);
                    if (!string.IsNullOrEmpty(_item.LocalPath) && File.Exists(_item.LocalPath))
                    {
                        string sel = "\"" + _item.LocalPath.Replace("\"", "\"\"") + "\"";
                        Process.Start("explorer.exe", "/select," + sel);
                    }
                    else if (!string.IsNullOrEmpty(dir))
                    {
                        Process.Start("explorer.exe", "\"" + dir.Replace("\"", "\"\"") + "\"");
                    }
                }
                catch { }
                Close();
            };

            foot.Controls.Add(openFile);
            foot.Controls.Add(openFolder);
        }

        private Label Row(string text, int y, Dictionary<string, string> lang)
        {
            Label l = Theme.Body(text, _dark);
            l.AutoEllipsis = true;
            l.SetBounds(20, y, 472, 20);
            return l;
        }

        private static string FmtSize(long b)
        {
            if (b <= 0) return "-";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.0") + " " + u[i];
        }

        private static string FmtTime(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "-";
            return string.Format("{0:00}:{1:00}:{2:00}",
                (int)t.TotalHours, t.Minutes, t.Seconds);
        }
    }
}
