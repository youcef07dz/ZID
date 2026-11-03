using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ZID
{
    internal sealed class BrowserInfo
    {
        public readonly string Name;
        public readonly string Path;
        public readonly bool Firefox;

        public BrowserInfo(string name, string path, bool firefox)
        {
            Name = name; Path = path; Firefox = firefox;
        }
    }

    // Detects installed browsers from the App Paths registry and the usual
    // install folders. Only well-known Chromium/Firefox-based forks are listed.
    internal static class Browsers
    {
        public static string ChromiumExtensionPath
        {
            get { return ChromiumExtensionPathFor(null); }
        }

        // Windows 7/8/8.1 and Chromium below 111 need the MV2 build
        // (the MV3 one uses world:"MAIN", added in Chrome 111).
        public static string ChromiumExtensionPathFor(string browserExe)
        {
            string modern = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zid-crx");
            if (!IsLegacyWindows() && !IsOldChromium(browserExe)) return modern;

            string legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zid-crx109");
            return File.Exists(Path.Combine(legacy, "manifest.json")) ? legacy : modern;
        }

        private static bool IsOldChromium(string browserExe)
        {
            if (string.IsNullOrEmpty(browserExe)) return false;
            try
            {
                int major = FileVersionInfo.GetVersionInfo(browserExe).FileMajorPart;
                return major > 0 && major < 111;
            }
            catch
            {
                return false;
            }
        }

        public static readonly string FirefoxExtensionPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zid-xpi");

        // CurrentMajorVersionNumber exists only on Windows 10+; Windows 7/8/8.1
        // have no such value. GetVersionEx is unusable here because an exe
        // without a compatibility manifest reports 6.2 on Windows 8.1/10.
        private static bool IsLegacyWindows()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object major = k.GetValue("CurrentMajorVersionNumber");
                        return !(major is int) || (int)major < 10;
                    }
                }
            }
            catch { }
            return false;
        }

        private sealed class Def
        {
            public readonly string Name;
            public readonly string Exe;
            public readonly bool Firefox;
            public readonly string[] Roots;

            public Def(string name, string exe, bool firefox, params string[] roots)
            {
                Name = name; Exe = exe; Firefox = firefox; Roots = roots;
            }
        }

        // Root prefixes: PF = Program Files, PF86 = Program Files (x86),
        // LAD = Local AppData.
        private static readonly Def[] Known = new Def[]
        {
            new Def("Google Chrome", "chrome.exe", false,
                @"PF\Google\Chrome\Application", @"PF86\Google\Chrome\Application", @"LAD\Google\Chrome\Application"),
            new Def("Microsoft Edge", "msedge.exe", false,
                @"PF86\Microsoft\Edge\Application", @"PF\Microsoft\Edge\Application"),
            new Def("Brave", "brave.exe", false,
                @"PF\BraveSoftware\Brave-Browser\Application", @"PF86\BraveSoftware\Brave-Browser\Application", @"LAD\BraveSoftware\Brave-Browser\Application"),
            new Def("Opera", "opera.exe", false,
                @"PF\Opera", @"PF86\Opera", @"LAD\Programs\Opera"),
            new Def("Opera GX", "opera.exe", false,
                @"LAD\Programs\Opera GX"),
            new Def("Vivaldi", "vivaldi.exe", false,
                @"PF\Vivaldi\Application", @"PF86\Vivaldi\Application", @"LAD\Vivaldi\Application"),
            new Def("Chromium", "chrome.exe", false,
                @"PF\Chromium\Application", @"PF86\Chromium\Application", @"LAD\Chromium\Application"),
            new Def("Yandex Browser", "browser.exe", false,
                @"LAD\Yandex\YandexBrowser\Application", @"PF\Yandex\YandexBrowser\Application"),
            new Def("Comodo Dragon", "dragon.exe", false,
                @"PF\Comodo\Dragon", @"PF86\Comodo\Dragon"),
            new Def("Slimjet", "slimjet.exe", false,
                @"PF\Slimjet", @"PF86\Slimjet"),
            new Def("SRWare Iron", "iron.exe", false,
                @"PF\SRWare Iron", @"PF86\SRWare Iron"),
            new Def("Mozilla Firefox", "firefox.exe", true,
                @"PF\Mozilla Firefox", @"PF86\Mozilla Firefox"),
            new Def("Firefox ESR", "firefox.exe", true,
                @"PF\Mozilla Firefox ESR", @"PF86\Mozilla Firefox ESR"),
            new Def("Waterfox", "waterfox.exe", true,
                @"PF\Waterfox", @"PF86\Waterfox"),
            new Def("LibreWolf", "librewolf.exe", true,
                @"PF\LibreWolf", @"PF86\LibreWolf"),
            new Def("Pale Moon", "palemoon.exe", true,
                @"PF\Pale Moon", @"PF86\Pale Moon")
        };

        public static List<BrowserInfo> Find(bool firefox)
        {
            List<BrowserInfo> found = new List<BrowserInfo>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> appPathUsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // Not in .NET 2.0's SpecialFolder enum.
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(pf86)) pf86 = pf;
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (Def d in Known)
            {
                if (d.Firefox != firefox) continue;
                string path = null;

                // One App Paths entry per exe name, so forks sharing an exe
                // (Chrome/Chromium, Firefox/ESR) do not both claim it.
                if (!appPathUsed.ContainsKey(d.Exe))
                {
                    path = FromAppPaths(d.Exe);
                    if (path != null) appPathUsed[d.Exe] = true;
                }

                if (path == null)
                {
                    foreach (string root in d.Roots)
                    {
                        string dir = ExpandRoot(root, pf, pf86, lad);
                        if (string.IsNullOrEmpty(dir)) continue;
                        string candidate = Path.Combine(dir, d.Exe);
                        if (File.Exists(candidate)) { path = candidate; break; }
                    }
                }

                if (path == null || seen.ContainsKey(path)) continue;
                seen[path] = true;
                found.Add(new BrowserInfo(d.Name, path, d.Firefox));
            }
            return found;
        }

        private static string ExpandRoot(string root, string pf, string pf86, string lad)
        {
            if (root.StartsWith("PF86\\", StringComparison.OrdinalIgnoreCase))
                return Combine(pf86, root.Substring(5));
            if (root.StartsWith("PF\\", StringComparison.OrdinalIgnoreCase))
                return Combine(pf, root.Substring(3));
            if (root.StartsWith("LAD\\", StringComparison.OrdinalIgnoreCase))
                return Combine(lad, root.Substring(4));
            return null;
        }

        private static string Combine(string baseDir, string rest)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            return Path.Combine(baseDir, rest);
        }

        private static string FromAppPaths(string exe)
        {
            RegistryKey[] roots = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            string[] subkeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe,
                @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exe
            };
            foreach (RegistryKey root in roots)
            {
                foreach (string sub in subkeys)
                {
                    try
                    {
                        using (RegistryKey k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string p = k.GetValue(null) as string;
                            if (string.IsNullOrEmpty(p)) continue;
                            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                            if (File.Exists(p)) return p;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
