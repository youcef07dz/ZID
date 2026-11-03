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
    internal sealed class BrowserListForm : Form
    {
        private readonly string _extensionDir;
        private readonly bool _firefox;
        private readonly bool _dark;
        private readonly Dictionary<string, string> _lang;

        public BrowserListForm(List<BrowserInfo> browsers, bool firefox,
                               Dictionary<string, string> lang, string theme)
        {
            _lang = lang ?? new Dictionary<string, string>();
            _firefox = firefox;
            _dark = (theme == "dark");
            _extensionDir = firefox ? Browsers.FirefoxExtensionPath : Browsers.ChromiumExtensionPath;

            Text = Lang.Get(_lang, firefox ? "bi.firefox" : "bi.chromium",
                            firefox ? "Firefox" : "Chromium browsers");
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            int rows = browsers.Count;
            if (rows < 1) rows = 1;
            if (rows > 8) rows = 8;
            ClientSize = new Size(470, 150 + rows * 36);

            Theme.Prepare(this, _dark);
            Theme.Header(this, _lang, _dark);
            Panel foot = Theme.Footer(this, _dark);

            Label prompt = Theme.MutedLabel(Lang.Get(_lang, "bi.pick",
                "Choose the browser to open the extension page in:"), _dark);
            prompt.SetBounds(18, Theme.HeaderHeight + 12, 434, 18);
            Controls.Add(prompt);

            if (browsers.Count == 0)
            {
                Panel card = Theme.Card(14, Theme.HeaderHeight + 32, 442, 44, _dark);
                Label none = Theme.MutedLabel(Lang.Get(_lang, "bi.none",
                    "No supported browser was found."), _dark);
                none.SetBounds(16, 12, 410, 20);
                card.Controls.Add(none);
                Controls.Add(card);
            }
            else
            {
                Panel list = new Panel();
                list.SetBounds(16, Theme.HeaderHeight + 36, 438, rows * 36);
                list.AutoScroll = true;
                list.BackColor = Theme.Window(_dark);
                int y = 0;
                foreach (BrowserInfo b in browsers)
                {
                    RoundButton open = (RoundButton)Theme.Button(b.Name, false, _dark);
                    open.TextAlign = ContentAlignment.MiddleLeft;
                    open.Badge = b.Name.Length > 0 ? b.Name.Substring(0, 1).ToUpperInvariant() : "?";
                    open.BadgeColor = Theme.Accent;
                    open.Chevron = true;
                    open.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                    open.SetBounds(0, y, 420, 34);
                    open.Tag = b;
                    open.Click += delegate(object sender, EventArgs e)
                    {
                        OpenBrowser((BrowserInfo)((Button)sender).Tag);
                    };
                    list.Controls.Add(open);
                    y += 36;
                }
                Controls.Add(list);
            }

            Button copy = Theme.Button(Lang.Get(_lang, "bi.copy_path",
                "Copy extension path"), false, _dark);
            copy.SetBounds(18, 13, 170, 32);
            Label copied = Theme.MutedLabel("", _dark);
            copied.Font = new Font("Segoe UI", 8f);
            copied.BackColor = Theme.Foot(_dark);
            copied.SetBounds(198, 20, 100, 18);
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(_extensionDir);
                    copied.Text = Lang.Get(_lang, "bi.copied", "Copied");
                }
                catch
                {
                    copied.Text = Lang.Get(_lang, "bi.copy_failed", "Copy failed");
                }
            };

            Button close = Theme.Button(Lang.Get(_lang, "btn.close", "Close"), false, _dark);
            close.SetBounds(348, 13, 104, 32);
            close.Click += delegate { Close(); };
            foot.Controls.Add(copy);
            foot.Controls.Add(copied);
            foot.Controls.Add(close);
            CancelButton = close;
            // Tab through the browser rows before the footer actions.
            foot.TabIndex = Controls.Count;
        }

        private void Alert(string text, MessageBoxIcon kind)
        {
            MsgBox.Show(this, text, Text, kind, _lang, _dark);
        }

        private void OpenBrowser(BrowserInfo browser)
        {
            try
            {
                string dir = _firefox ? _extensionDir
                                      : Browsers.ChromiumExtensionPathFor(browser.Path);
                string manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                {
                    Alert(Lang.Get(_lang, "bi.local_missing",
                                   "Local extension folder not found:") +
                          "\n" + dir, MessageBoxIcon.Error);
                    return;
                }

                if (_firefox)
                {
                    Process.Start(browser.Path, "about:debugging#/runtime/this-firefox");
                    Alert(Lang.Get(_lang, "bi.firefox_local",
                                   "In about:debugging, click Load Temporary Add-on and select:") +
                          "\n" + manifest, MessageBoxIcon.Information);
                }
                else if (browser.Path.IndexOf(@"\Google\Chrome\",
                             StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Process.Start(browser.Path, "chrome://extensions/");
                    Alert(Lang.Get(_lang, "bi.chrome_manual",
                                   "Google Chrome 137+ blocks --load-extension. In the Extensions page, enable Developer mode, click \"Load unpacked\", and select:") +
                          "\n" + dir, MessageBoxIcon.Information);
                }
                else
                {
                    string exeName = Path.GetFileNameWithoutExtension(browser.Path);
                    if (Process.GetProcessesByName(exeName).Length > 0)
                    {
                        Alert(Lang.Get(_lang, "bi.browser_running",
                                       "Close the browser completely, then choose it again to load the local extension:") +
                              "\n" + browser.Name, MessageBoxIcon.Warning);
                        return;
                    }
                    Process.Start(browser.Path, "--load-extension=\"" + dir + "\"");
                }
                Close();
            }
            catch (Exception ex)
            {
                Alert(Lang.Get(_lang, "bi.open_failed", "Cannot start the browser:") +
                      "\n" + ex.Message, MessageBoxIcon.Error);
            }
        }
    }
}
