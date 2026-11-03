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
    internal sealed class BrowserIntegrationForm : Form
    {
        private readonly Dictionary<string, string> _lang;
        private readonly bool _dark;

        public BrowserIntegrationForm(Dictionary<string, string> lang, string theme)
        {
            _lang = lang ?? new Dictionary<string, string>();
            _dark = (theme == "dark");
            Text = Lang.Get(_lang, "bi.title", "Browser integration");
            ClientSize = new Size(460, 300);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Theme.Prepare(this, _dark);
            Theme.Header(this, _lang, _dark);
            Panel foot = Theme.Footer(this, _dark);

            Label intro = Theme.MutedLabel(Lang.Get(_lang, "bi.intro",
                "Choose your browser family. The extension page will open in the browser you pick."), _dark);
            intro.SetBounds(18, Theme.HeaderHeight + 12, 424, 40);
            Controls.Add(intro);

            RoundButton firefox = (RoundButton)Theme.Button(
                Lang.Get(_lang, "bi.firefox", "Firefox"), false, _dark);
            firefox.TextAlign = ContentAlignment.MiddleLeft;
            firefox.Badge = "F";
            firefox.BadgeColor = Color.FromArgb(249, 115, 22);
            firefox.SubText = "Firefox / ESR / Waterfox";
            firefox.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            firefox.SetBounds(18, 96, 200, 76);
            firefox.Click += delegate { ShowBrowsers(true); };
            Controls.Add(firefox);

            RoundButton chromium = (RoundButton)Theme.Button(
                Lang.Get(_lang, "bi.chromium", "Chromium browsers"), false, _dark);
            chromium.TextAlign = ContentAlignment.MiddleLeft;
            chromium.Badge = "C";
            chromium.BadgeColor = Color.FromArgb(59, 130, 246);
            chromium.SubText = "Chrome, Edge, Brave";
            chromium.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            chromium.SetBounds(230, 96, 200, 76);
            chromium.Click += delegate { ShowBrowsers(false); };
            Controls.Add(chromium);

            Button close = Theme.Button(Lang.Get(_lang, "btn.close", "Close"), false, _dark);
            close.SetBounds(346, 13, 96, 32);
            close.Click += delegate { Close(); };
            foot.Controls.Add(close);
            CancelButton = close;
            // Tab through the choices before the footer actions.
            foot.TabIndex = Controls.Count;
        }

        private void ShowBrowsers(bool firefox)
        {
            List<BrowserInfo> found = Browsers.Find(firefox);
            using (BrowserListForm f = new BrowserListForm(found, firefox, _lang, _dark ? "dark" : "light"))
                f.ShowDialog(this);
        }
    }
}
