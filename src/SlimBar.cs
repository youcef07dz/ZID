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
    internal sealed class SlimBar : Control
    {
        public static Color StartColor = Color.FromArgb(129, 140, 248);
        public static Color EndColor = Color.FromArgb(34, 211, 238);

        private int _value;
        private bool _dark;

        public bool Dark
        {
            get { return _dark; }
            set { _dark = value; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public SlimBar()
        {
            TabStop = false;  // a progress bar must not take keyboard focus
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Math.Max(2, r.Height / 2);

            using (GraphicsPath trackPath = Theme.Rounded(r, radius))
            {
                using (SolidBrush track = new SolidBrush(_dark ? Color.FromArgb(26, 33, 48)
                                                               : Color.FromArgb(39, 48, 65)))
                    g.FillPath(track, trackPath);

                int w = (int)Math.Round((Width - 2) * _value / 100.0);
                if (w > 0)
                {
                    Rectangle fill = new Rectangle(1, 1, Math.Max(1, w), Math.Max(1, Height - 2));
                    using (GraphicsPath fillPath = Theme.Rounded(fill, Math.Max(2, fill.Height / 2)))
                    {
                        using (LinearGradientBrush br = new LinearGradientBrush(
                                   new Rectangle(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2)),
                                   StartColor, EndColor, LinearGradientMode.Horizontal))
                            g.FillPath(br, fillPath);
                        using (Pen sheen = new Pen(Color.FromArgb(46, 255, 255, 255)))
                            g.DrawLine(sheen, fill.X + radius, fill.Y + 1, fill.Right - radius, fill.Y + 1);
                    }
                }

                using (Pen edge = new Pen(_dark ? Color.FromArgb(39, 48, 62)
                                                : Color.FromArgb(51, 64, 85)))
                    g.DrawPath(edge, trackPath);
            }
        }
    }
}
