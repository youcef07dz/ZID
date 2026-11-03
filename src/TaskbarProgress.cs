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
    //  Taskbar progress (Windows 7+; no-op elsewhere)
    // =====================================================================
    internal static class TaskbarProgress
    {
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList { }

        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int state);
        }

        private static ITaskbarList3 _list;
        private static bool _failed;

        public static void Set(IntPtr hwnd, int state, int percent)
        {
            if (_failed) return;
            try
            {
                if (_list == null)
                {
                    _list = (ITaskbarList3)new TaskbarList();
                    _list.HrInit();
                }
                _list.SetProgressState(hwnd, state);
                if (state != 0)
                    _list.SetProgressValue(hwnd, (ulong)(percent < 0 ? 0 : (percent > 100 ? 100 : percent)), 100UL);
            }
            catch { _failed = true; _list = null; }
        }
    }
}
