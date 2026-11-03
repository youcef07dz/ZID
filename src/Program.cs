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
    //  Entry point
    // =====================================================================
    internal static class Program
    {
        private const string InstanceMutexName = "Global\\ZID_SingleInstance";
        private const string ShowEventName = "ZID_Show";
        private static Mutex _instanceMutex;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        internal static void ReportCrash(string where, Exception ex)
        {
            Logger.Error("CRASH " + where + ": " + ex);
            try
            {
                MessageBox.Show("Unexpected error in " + where + ":\r\n\r\n" +
                    ex.Message + "\r\n\r\nDetails were written to app.log.",
                    "ZyZoom Internet Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        // WebBrowser defaults to IE7 mode. Pick a mode the installed IE supports;
        // an unsupported value (e.g. 11001 on IE8) is ignored and the UI breaks.
        private static void ConfigureBrowserEmulation()
        {
            int major = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\Internet Explorer"))
                {
                    if (k != null)
                    {
                        string v = k.GetValue("svcVersion") as string;
                        if (string.IsNullOrEmpty(v)) v = k.GetValue("Version") as string;
                        if (!string.IsNullOrEmpty(v))
                            int.TryParse(v.Split('.')[0], out major);
                    }
                }
            }
            catch { }

            int mode = major >= 11 ? 11001
                     : major == 10 ? 10001
                     : major == 9  ? 9999
                     : major == 8  ? 8888
                     : 7000;
            try
            {
                Registry.SetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION",
                    Path.GetFileName(Application.ExecutablePath), mode, RegistryValueKind.DWord);
            }
            catch { }
        }

        [STAThread]
        private static void Main()
        {
            // Must run before any window exists; keeps the UI crisp on high-DPI screens.
            try { SetProcessDPIAware(); } catch { }

            AppPaths.Init();
            Logger.Init(Path.Combine(AppPaths.DataDir, "app.log"));
            Logger.Info("data folder: " + AppPaths.DataDir +
                        (AppPaths.Portable ? " (portable)" : ""));
            if (AppPaths.Notice.Length > 0) Logger.Warn(AppPaths.Notice);

            bool createdNew;
            try
            {
                _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                try { _instanceMutex = new Mutex(true, "ZID_SingleInstance", out createdNew); }
                catch { createdNew = true; }
            }

            if (!createdNew)
            {
                // The first instance may still be creating its show event, so
                // retry briefly instead of dropping a zid://open launch.
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        using (EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            show.Set();
                            return;
                        }
                    }
                    catch { }
                    Thread.Sleep(100);
                }

                IntPtr existing = FindWindow(null, "ZyZoom Internet Downloader");
                if (existing != IntPtr.Zero)
                {
                    ShowWindow(existing, 9); // SW_RESTORE
                    SetForegroundWindow(existing);
                }
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                ReportCrash("UI", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                ReportCrash("background", e.ExceptionObject as Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ConfigureBrowserEmulation();
            MainForm form;
            try
            {
                form = new MainForm();
            }
            catch (Exception ex)
            {
                ReportCrash("startup", ex);
                return;
            }
            try
            {
                EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                Thread listener = new Thread(delegate ()
                {
                    while (true)
                    {
                        try { show.WaitOne(); }
                        catch { return; }
                        try { form.BeginInvoke(new MethodInvoker(form.RestoreFromTray)); }
                        catch { }
                    }
                });
                listener.IsBackground = true;
                listener.Name = "Show window";
                listener.Start();
            }
            catch { }
            try
            {
                Application.Run(form);
            }
            catch (Exception ex)
            {
                ReportCrash("main loop", ex);
            }
            GC.KeepAlive(_instanceMutex);
        }
    }
}
