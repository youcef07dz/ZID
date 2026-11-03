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
    //  Clipboard chain watcher (WinForms has no clipboard-change event)
    // =====================================================================
    internal sealed class ClipWatcher : NativeWindow
    {
        private const int WM_DRAWCLIPBOARD = 0x0308;
        private const int WM_CHANGECBQ     = 0x030D;

        [DllImport("user32.dll")] private static extern IntPtr SetClipboardViewer(IntPtr hWndNewViewer);
        [DllImport("user32.dll")] private static extern bool ChangeClipboardChain(IntPtr hWndRemove, IntPtr hWndNewNext);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly MainForm _owner;
        private IntPtr _next;

        public ClipWatcher(MainForm owner) { _owner = owner; }

        public void Hook()   { _next = SetClipboardViewer(Handle); }
        public void Unhook()
        {
            try { ChangeClipboardChain(Handle, _next); } catch { }
            ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DRAWCLIPBOARD)
            {
                if (_next != IntPtr.Zero) SendMessage(_next, WM_DRAWCLIPBOARD, IntPtr.Zero, IntPtr.Zero);
                // Never let a clipboard fault escape a native window callback.
                try { _owner.OnClipboardChanged(); }
                catch (Exception ex) { Program.ReportCrash("clipboard", ex); }
            }
            else if (m.Msg == WM_CHANGECBQ)
            {
                _next = m.WParam;
                if (_next != IntPtr.Zero) SendMessage(_next, WM_CHANGECBQ, m.WParam, m.LParam);
            }
            base.WndProc(ref m);
        }
    }
}
