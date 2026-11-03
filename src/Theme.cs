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
    //  Shared WinForms theme, aligned with the HTML UI's dark palette.
    // =====================================================================
    internal static class Theme
    {
        public const int HeaderHeight = 40;
        public const int FooterHeight = 58;

        private static Color D(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static Color Window(bool dark)    { return D(23, 31, 56); }    // #171f38
        public static Color Frame(bool dark)     { return D(18, 26, 40); }    // #121a28
        public static Color Text(bool dark)      { return D(255, 255, 255); } // #ffffff
        public static Color Muted(bool dark)     { return D(201, 212, 230); } // #c9d4e6
        public static Color Field(bool dark)     { return D(18, 26, 40); }    // #121a28
        public static Color Border(bool dark)    { return D(51, 64, 90); }    // #33405a
        public static Color BorderSoft(bool dark){ return D(39, 49, 73); }    // #273149
        public static Color Panel(bool dark)     { return D(26, 33, 64); }    // #1a2140
        public static Color Foot(bool dark)      { return D(23, 31, 56); }    // #171f38
        public static Color FootBorder(bool dark){ return D(51, 64, 90); }    // #33405a
        public static Color Header1(bool dark)   { return D(35, 44, 64); }    // #232c40
        public static Color Header2(bool dark)   { return D(41, 35, 67); }    // #292343

        private static Color _accent = D(99, 102, 241);
        private static Color _accent2 = D(139, 92, 246);
        private static Color _accentHover = D(129, 140, 248);
        private static Color _accentHover2 = D(167, 139, 250);
        private static Color _accentBorder = D(79, 70, 229);

        public static Color Accent             { get { return _accent; } }       // #6366f1 by default
        public static Color Accent2            { get { return _accent2; } }      // #8b5cf6 by default
        public static Color AccentHover        { get { return _accentHover; } }  // #818cf8 by default
        public static Color AccentHover2       { get { return _accentHover2; } } // #a78bfa by default
        public static Color AccentBorder       { get { return _accentBorder; } } // #4f46e5 by default
        public static Color Teal               { get { return D(34, 211, 238); } }  // #22d3ee
        public static Color Ok                 { get { return D(52, 211, 153); } }  // #34d399
        public static Color Warn               { get { return D(251, 191, 36); } }   // #fbbf24
        public static Color Danger             { get { return D(244, 63, 94); } }    // #f43f5e

        // The page owns the accent choice; every native dialog follows it.
        public static void SetAccent(Color c)
        {
            _accent = c;
            _accent2 = Mix(c, Color.White, 0.22);
            _accentBorder = Mix(c, Color.Black, 0.28);
            _accentHover = Mix(c, Color.White, 0.28);
            _accentHover2 = Mix(_accent2, Color.White, 0.28);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private static float _scale = -1f;

        // Process is system-DPI aware (Program.Main); scale the native chrome by
        // the monitor DPI. The HTML page is zoomed separately by the host.
        public static float Scale
        {
            get
            {
                if (_scale < 0)
                {
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                            _scale = g.DpiX / 96f;
                    }
                    catch { _scale = 1f; }
                    if (_scale < 1f) _scale = 1f;
                    if (_scale > 2f) _scale = 2f;
                }
                return _scale;
            }
        }

        public static int Px(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        public static void Prepare(Form f, bool dark)
        {
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.AutoScaleDimensions = new SizeF(96f, 96f);
            f.Font = new Font("Segoe UI", 9f);
            f.BackColor = Window(dark);
            f.ForeColor = Text(dark);
            // Borderless windows have no system frame, so draw a soft 1px outline.
            AddFrame(f, dark);
            RoundCorners(f);
            // Borderless windows lose the system menu, so Alt+F4 would do nothing.
            f.KeyPreview = true;
            f.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Alt && e.KeyCode == Keys.F4) { f.Close(); e.Handled = true; }
            };
        }

        // Dialogs use the HTML UI's 12px corner radius; the main window stays square.
        private static void RoundCorners(Form f)
        {
            EventHandler apply = delegate
            {
                if (f.Width < 24 || f.Height < 24) return;
                int r = Px(12);
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddArc(0, 0, r, r, 180, 90);
                    p.AddArc(f.Width - r - 1, 0, r, r, 270, 90);
                    p.AddArc(f.Width - r - 1, f.Height - r - 1, r, r, 0, 90);
                    p.AddArc(0, f.Height - r - 1, r, r, 90, 90);
                    p.CloseFigure();
                    f.Region = new Region(p);
                }
            };
            f.Resize += apply;
            apply(null, EventArgs.Empty);
        }

        private static void AddFrame(Form f, bool dark)
        {
            DockStyle[] sides = { DockStyle.Top, DockStyle.Bottom, DockStyle.Left, DockStyle.Right };
            foreach (DockStyle side in sides)
            {
                Panel edge = new Panel();
                edge.BackColor = Border(dark);
                edge.Dock = side;
                if (side == DockStyle.Top || side == DockStyle.Bottom) edge.Height = 1;
                else edge.Width = 1;
                f.Controls.Add(edge);
                f.Controls.SetChildIndex(edge, 0);
            }
        }

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static Panel Header(Form f, Dictionary<string, string> lang, bool dark)
        {
            Panel head = new Panel();
            head.BackColor = Header1(dark);
            head.SetBounds(0, 0, f.ClientSize.Width, HeaderHeight);
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (LinearGradientBrush br = new LinearGradientBrush(
                           head.ClientRectangle, Header1(dark), Header2(dark), LinearGradientMode.Horizontal))
                    g.FillRectangle(br, head.ClientRectangle);

                // 2px brand strip along the top, like the HTML titlebar.
                using (LinearGradientBrush accent = new LinearGradientBrush(
                           new Rectangle(0, 0, Math.Max(1, head.Width), 2),
                           Accent, Teal, LinearGradientMode.Horizontal))
                    g.FillRectangle(accent, 0, 0, head.Width, 2);

                using (Pen edge = new Pen(BorderSoft(dark)))
                    g.DrawLine(edge, 0, head.Height - 1, head.Width, head.Height - 1);

                using (SolidBrush dot = new SolidBrush(Teal))
                    g.FillEllipse(dot, 16, 15, 9, 9);
                using (Pen ring = new Pen(Color.FromArgb(70, Teal)))
                    g.DrawEllipse(ring, 14, 13, 13, 13);

                int chrome = f.MinimizeBox ? 76 : 42;
                using (Font font = new Font("Segoe UI", 10.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g,
                        Lang.Get(lang ?? new Dictionary<string, string>(),
                                 "app.title", "ZyZoom Internet Downloader"),
                        font, new Rectangle(34, 2, Math.Max(10, head.Width - 34 - chrome), HeaderHeight - 2),
                        Color.FromArgb(241, 245, 251),
                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
            };

            // The header doubles as the title bar: drag it to move the window.
            head.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(f.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            };

            int x = head.Width - 36;
            Button close = ChromeButton("\u00d7", dark, Color.FromArgb(220, 38, 38));
            close.SetBounds(x, 6, 28, 28);
            close.Click += delegate { f.Close(); };
            head.Controls.Add(close);

            if (f.MinimizeBox)
            {
                Button min = ChromeButton("\u2013", dark, Color.FromArgb(47, 58, 78));
                min.SetBounds(x - 32, 6, 28, 28);
                min.Click += delegate { f.WindowState = FormWindowState.Minimized; };
                head.Controls.Add(min);
            }

            f.Controls.Add(head);
            return head;
        }

        public static Button ChromeButton(string text, bool dark, Color hover)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Radius = Px(6);
            b.BackColor = Header2(dark);
            b.CornerColor = Header2(dark);
            b.HoverBack = hover;
            b.DownBack = hover;
            b.ForeColor = Color.FromArgb(174, 185, 204);
            b.Font = new Font("Segoe UI", 11f);
            b.TabStop = false;
            return b;
        }

        public static Panel Footer(Form f, bool dark)
        {
            Panel foot = new Panel();
            foot.SetBounds(0, f.ClientSize.Height - FooterHeight, f.ClientSize.Width, FooterHeight);
            foot.BackColor = Foot(dark);
            foot.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(FootBorder(dark)))
                    e.Graphics.DrawLine(p, 0, 0, foot.Width, 0);
                using (Pen p = new Pen(Color.FromArgb(22, 255, 255, 255)))
                    e.Graphics.DrawLine(p, 0, 1, foot.Width, 1);
            };
            f.Controls.Add(foot);
            return foot;
        }

        public static Panel Card(int x, int y, int w, int h, bool dark)
        {
            CardPanel p = new CardPanel();
            p.Dark = dark;
            p.SetBounds(x, y, w, h);
            p.BackColor = Panel(dark);
            return p;
        }

        public static Label Section(string text, bool dark)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            l.ForeColor = Muted(dark);
            return l;
        }

        public static Label Body(string text, bool dark)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Text(dark);
            return l;
        }

        public static Label MutedLabel(string text, bool dark)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Muted(dark);
            return l;
        }

        // Rounded field host: a borderless TextBox inside a painted 1px border.
        public static TextField TextField(int x, int y, int w, bool dark)
        {
            TextField t = new TextField(dark);
            t.SetBounds(x, y, w, 32);
            return t;
        }

        // Kept for callers that need a plain stock TextBox (none in the dialogs).
        public static TextBox FieldBox(int x, int y, int w, bool dark)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 26);
            t.AutoSize = false;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Field(dark);
            t.ForeColor = Text(dark);
            t.Font = new Font("Segoe UI", 9.5f);
            return t;
        }

        public static Button Button(string text, bool primary, bool dark)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Radius = Px(8);
            b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
            if (primary)
            {
                b.BackColor = Accent;
                b.BackColor2 = Accent2;
                b.ForeColor = Color.White;
                b.BorderColor = AccentBorder;
                b.HoverBack = AccentHover;
                b.HoverBack2 = AccentHover2;
                b.DownBack = AccentBorder;
                b.DownBack2 = Accent;
            }
            else
            {
                ApplySecondary(b, dark);
            }
            return b;
        }

        // Secondary buttons carry the same tint as the page's toolbar/modal buttons.
        public static void ApplySecondary(RoundButton b, bool dark)
        {
            b.BackColor = Mix(D(29, 38, 56), _accent, 0.30);
            b.BackColor2 = Mix(D(33, 42, 62), _accent2, 0.28);
            b.ForeColor = Color.White;
            b.BorderColor = Mix(Border(dark), _accent, 0.40);
            b.HoverBack = Mix(D(40, 50, 71), _accent, 0.38);
            b.HoverBack2 = Mix(D(48, 58, 82), _accent2, 0.34);
            b.DownBack = Mix(D(24, 32, 47), _accent, 0.30);
            b.DownBack2 = Mix(D(30, 39, 55), _accent2, 0.30);
        }

        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------------
    //  Rounded flat button with optional gradient, badge, sub-label, chevron.
    // ---------------------------------------------------------------------
    internal sealed class RoundButton : Button
    {
        public int Radius = 8;
        public Color BackColor2 = Color.Empty;
        public Color HoverBack;
        public Color HoverBack2 = Color.Empty;
        public Color DownBack;
        public Color DownBack2 = Color.Empty;
        public Color BorderColor = Color.Empty;
        public Color CornerColor = Color.Empty;
        public Color BadgeColor = Color.Empty;
        public string Badge = "";
        public string SubText = "";
        public bool Chevron;

        private bool _hover, _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color corner = CornerColor.IsEmpty ? (Parent != null ? Parent.BackColor : BackColor) : CornerColor;
            using (SolidBrush bg = new SolidBrush(corner)) g.FillRectangle(bg, ClientRectangle);

            Color back = BackColor, back2 = BackColor2, fore = ForeColor;
            if (!Enabled)
            {
                back = Color.FromArgb(24, 32, 47);
                back2 = Color.FromArgb(28, 37, 53);
                fore = Color.FromArgb(160, 173, 194);
            }
            else if (_down && !DownBack.IsEmpty)
            {
                back = DownBack;
                if (!DownBack2.IsEmpty) back2 = DownBack2;
            }
            else if (_hover && !HoverBack.IsEmpty)
            {
                back = HoverBack;
                if (!HoverBack2.IsEmpty) back2 = HoverBack2;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Radius))
            {
                if (back2.IsEmpty || back2 == back)
                {
                    using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, path);
                }
                else
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(
                               new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                               back, back2, LinearGradientMode.Horizontal))
                        g.FillPath(b, path);
                }

                if (!BorderColor.IsEmpty)
                    using (Pen p = new Pen(BorderColor)) g.DrawPath(p, path);

                if (Height > 12)
                {
                    using (Pen sheen = new Pen(Color.FromArgb(26, 255, 255, 255)))
                        g.DrawLine(sheen, r.X + Math.Min(8, r.Width / 2), r.Y + 1,
                                          r.Right - Math.Min(8, r.Width / 2), r.Y + 1);
                }

                if (Focused)
                    using (Pen p = new Pen(Color.FromArgb(190, Theme.AccentHover)))
                    {
                        using (GraphicsPath focus = Theme.Rounded(
                                   new Rectangle(2, 2, Width - 5, Height - 5), Math.Max(2, Radius - 2)))
                            g.DrawPath(p, focus);
                    }
            }

            int left = 12;
            if (Badge.Length > 0)
            {
                int size = Math.Min(26, Height - 12);
                Rectangle br = new Rectangle(11, (Height - size) / 2, size, size);
                Color badge = BadgeColor.IsEmpty ? Theme.Accent : BadgeColor;
                using (GraphicsPath bp = Theme.Rounded(br, 7))
                using (SolidBrush bb = new SolidBrush(badge))
                    g.FillPath(bb, bp);
                using (Font bf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, Badge, bf, br, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
                left = br.Right + 10;
            }

            int right = Chevron ? 22 : 0;
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                    TextFormatFlags.NoPadding;
            Rectangle tr = r;
            if (TextAlign == ContentAlignment.MiddleLeft)
            {
                flags |= TextFormatFlags.Left;
                tr = new Rectangle(left, r.Y, Math.Max(1, Width - left - right), r.Height);
            }
            else if (TextAlign == ContentAlignment.MiddleRight)
            {
                flags |= TextFormatFlags.Right;
                tr = new Rectangle(r.X, r.Y, Math.Max(1, Width - right - 10), r.Height);
            }
            else
            {
                flags |= TextFormatFlags.HorizontalCenter;
                tr = new Rectangle(r.X, r.Y, Math.Max(1, Width - right), r.Height);
            }

            if (SubText.Length > 0)
            {
                int ty = r.Y + Math.Max(0, (r.Height - 34) / 2);
                TextRenderer.DrawText(g, Text, Font,
                    new Rectangle(tr.X, ty, tr.Width, 20), fore, flags);
                using (Font sf = new Font("Segoe UI", 8.25f))
                    TextRenderer.DrawText(g, SubText, sf,
                        new Rectangle(tr.X, ty + 19, tr.Width, 16),
                        Color.FromArgb(148, 161, 181), flags);
            }
            else
            {
                TextRenderer.DrawText(g, Text, Font, tr, fore, flags);
            }

            if (Chevron)
            {
                using (SolidBrush ch = new SolidBrush(Color.FromArgb(120, 136, 160)))
                using (Font cf = new Font("Segoe UI", 12f))
                    TextRenderer.DrawText(g, "\u203a", cf,
                        new Rectangle(Width - 26, 0, 16, Height), ch.Color,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Rounded 1px field border around a borderless TextBox.
    // ---------------------------------------------------------------------
    internal sealed class TextField : Panel
    {
        public readonly TextBox Box;
        private readonly bool _dark;
        private bool _focus;

        public TextField(bool dark)
        {
            _dark = dark;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Field(dark);

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Field(dark);
            Box.ForeColor = Theme.Text(dark);
            Box.Font = new Font("Segoe UI", 9.5f);
            Box.AutoSize = false;
            Box.Height = 20;
            Box.GotFocus += delegate { _focus = true; Invalidate(); };
            Box.LostFocus += delegate { _focus = false; Invalidate(); };
            Controls.Add(Box);
            LayoutBox();
            Cursor = Cursors.IBeam;
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        public bool ReadOnly
        {
            get { return Box.ReadOnly; }
            set
            {
                Box.ReadOnly = value;
                Box.BackColor = value ? Theme.Window(_dark) : Theme.Field(_dark);
                BackColor = Box.BackColor;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        private void LayoutBox()
        {
            if (Box == null) return;
            Box.SetBounds(11, Math.Max(1, (Height - Box.Height) / 2), Math.Max(1, Width - 22), Box.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parent = Parent != null ? Parent.BackColor : Theme.Window(_dark);
            using (SolidBrush bg = new SolidBrush(parent)) g.FillRectangle(bg, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Theme.Px(8)))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) g.FillPath(b, path);
                using (Pen p = new Pen(_focus ? Theme.Accent : Theme.Border(_dark)))
                    g.DrawPath(p, path);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  Rounded panel used to group dialog content.
    // ---------------------------------------------------------------------
    internal sealed class CardPanel : Panel
    {
        public bool Dark = true;

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color parent = Parent != null ? Parent.BackColor : Theme.Window(Dark);
            using (SolidBrush bg = new SolidBrush(parent)) g.FillRectangle(bg, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Rounded(r, Theme.Px(10)))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border(Dark))) g.DrawPath(p, path);
            }
        }
    }
}
