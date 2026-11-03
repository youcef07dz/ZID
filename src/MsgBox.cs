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
    //  Themed replacement for the stock Windows MessageBox.
    // =====================================================================
    internal sealed class MsgBox : Form
    {
        public static DialogResult Show(IWin32Window owner, string text, string title,
                                        MessageBoxIcon kind,
                                        Dictionary<string, string> lang, bool dark)
        {
            using (MsgBox f = new MsgBox(text, title, kind, lang, dark, false, null, null, null))
                return owner == null ? f.ShowDialog() : f.ShowDialog(owner);
        }

        // Two buttons: the primary returns OK, the secondary returns Cancel.
        public static DialogResult Confirm(IWin32Window owner, string text, string title,
                                           string okText, string cancelText,
                                           Dictionary<string, string> lang, bool dark)
        {
            using (MsgBox f = new MsgBox(text, title, MessageBoxIcon.Question, lang, dark,
                                         true, okText, cancelText, null))
                return owner == null ? f.ShowDialog() : f.ShowDialog(owner);
        }

        // More than two choices; returns the index of the clicked button, or -1.
        public static int Choose(IWin32Window owner, string text, string title, string[] choices,
                                 Dictionary<string, string> lang, bool dark)
        {
            using (MsgBox f = new MsgBox(text, title, MessageBoxIcon.Question, lang, dark,
                                         false, null, null, choices))
            {
                if (owner == null) f.ShowDialog(); else f.ShowDialog(owner);
                return f._choice;
            }
        }

        private int _choice;

        private MsgBox(string text, string title, MessageBoxIcon kind,
                       Dictionary<string, string> lang, bool dark, bool confirm,
                       string okText, string cancelText, string[] choices)
        {
            _choice = -1;
            lang = lang ?? new Dictionary<string, string>();
            const int Width = 460;
            const int CardPad = 16;
            int textWidth = 350;

            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Theme.Prepare(this, dark);

            Size measured = TextRenderer.MeasureText(text, Font,
                new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
            int bodyHeight = Math.Max(38, Math.Min(310, measured.Height + 6));
            ClientSize = new Size(Width, Theme.HeaderHeight + bodyHeight + CardPad * 2 + Theme.FooterHeight);

            Theme.Header(this, lang, dark);
            Panel foot = Theme.Footer(this, dark);

            Color tone = kind == MessageBoxIcon.Error   ? Theme.Danger
                       : kind == MessageBoxIcon.Warning ? Theme.Warn
                       : kind == MessageBoxIcon.Question ? Theme.Accent
                       : Theme.Teal;
            string glyph = kind == MessageBoxIcon.Question ? "?"
                         : kind == MessageBoxIcon.Information ? "i" : "!";

            Panel card = Theme.Card(12, Theme.HeaderHeight + 10, Width - 24, bodyHeight + CardPad * 2, dark);
            Controls.Add(card);

            Panel badge = new Panel();
            badge.SetBounds(CardPad, CardPad, 36, 36);
            badge.BackColor = Theme.Panel(dark);
            badge.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, 35, 35);
                using (GraphicsPath p = Theme.Rounded(r, 10))
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                               new Rectangle(0, 0, 35, 35), Lighten(tone, 36), tone,
                               LinearGradientMode.Vertical))
                        e.Graphics.FillPath(b, p);
                    using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255)))
                        e.Graphics.DrawPath(edge, p);
                }
                using (Font f = new Font("Segoe UI", 11.5f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, glyph, f, r, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
            };
            card.Controls.Add(badge);

            bool scroll = measured.Height + 6 > bodyHeight;
            Panel body = new Panel();
            body.SetBounds(68, CardPad, textWidth + 4, bodyHeight);
            body.BackColor = Theme.Panel(dark);
            body.AutoScroll = scroll;
            Label msg = Theme.Body(text, dark);
            msg.UseMnemonic = false;
            msg.AutoSize = true;
            msg.MaximumSize = new Size(
                textWidth - (scroll ? SystemInformation.VerticalScrollBarWidth + 2 : 0), 0);
            msg.Location = new Point(0, 0);
            body.Controls.Add(msg);
            card.Controls.Add(body);

            string yes = okText ?? Lang.Get(lang, "btn.ok", "OK");
            if (choices != null && choices.Length > 0)
            {
                // Right-aligned: the last choice (Cancel) sits rightmost.
                int x = Width - 18;
                for (int i = choices.Length - 1; i >= 0; i--)
                {
                    int w = TextRenderer.MeasureText(choices[i], Font).Width + 26;
                    if (w < 76) w = 76;
                    Button b = Theme.Button(choices[i], i == 0, dark);
                    b.SetBounds(x - w, 13, w, 32);
                    int pick = i;
                    b.Click += delegate { _choice = pick; DialogResult = DialogResult.OK; Close(); };
                    foot.Controls.Add(b);
                    if (i == 0) AcceptButton = b;
                    if (i == choices.Length - 1) CancelButton = b;
                    x -= w + 8;
                }
                return;
            }

            Button ok = Theme.Button(yes, true, dark);
            ok.SetBounds(Width - 18 - 92, 13, 92, 32);
            ok.DialogResult = DialogResult.OK;
            foot.Controls.Add(ok);
            AcceptButton = ok;

            if (!confirm)
            {
                CancelButton = ok;
            }
            else
            {
                Button no = Theme.Button(cancelText ?? Lang.Get(lang, "btn.cancel", "Cancel"), false, dark);
                no.SetBounds(Width - 18 - 92, 13, 92, 32);
                no.DialogResult = DialogResult.Cancel;
                foot.Controls.Add(no);
                ok.SetBounds(Width - 18 - 92 - 8 - 92, 13, 92, 32);
                CancelButton = no;
            }
        }

        private static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(
                Math.Min(255, c.R + amount),
                Math.Min(255, c.G + amount),
                Math.Min(255, c.B + amount));
        }
    }
}
