using System;
using System.IO;

namespace ZID
{
    // Decides where state, config, the CA cache and the log live. An existing
    // %AppData%\ZID install keeps AppData; a fresh copy in a writable folder
    // stays self-contained under data\ (portable); a read-only install falls
    // back to AppData and logs a notice.
    internal static class AppPaths
    {
        // The pre-rename profile; copied once so settings and state survive.
        private const string LegacyFolder = "IdmStyleApp";

        private static string _dataDir;
        private static bool _portable;
        private static string _notice = "";

        internal static bool Portable { get { return _portable; } }
        internal static string Notice { get { return _notice; } }

        internal static string DataDir
        {
            get
            {
                if (_dataDir == null) Init();
                return _dataDir;
            }
        }

        // Pure so tests cover the decision without touching disk.
        internal static bool UsePortable(bool localWritable, bool existingAppData)
        {
            return localWritable && !existingAppData;
        }

        internal static void Init()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _notice = "";

            // Test/support hook: an absolute ZID_DATA_DIR keeps a run away from
            // the real %AppData%\ZID profile (recovery smoke test).
            string forced = Environment.GetEnvironmentVariable("ZID_DATA_DIR");
            if (!string.IsNullOrEmpty(forced) && Path.IsPathRooted(forced))
            {
                try
                {
                    string dir = Path.GetFullPath(forced);
                    Directory.CreateDirectory(dir);
                    _portable = true;
                    _dataDir = dir;
                    return;
                }
                catch { }
            }

            string appData = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "ZID");
            string legacy = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), LegacyFolder);

            bool migrate = !Directory.Exists(appData) && Directory.Exists(legacy);
            bool existing = Directory.Exists(appData) || migrate;
            bool writable = CanWrite(exeDir);
            _portable = UsePortable(writable, existing);

            if (_portable)
            {
                string data = Path.Combine(exeDir, "data");
                try
                {
                    Directory.CreateDirectory(data);
                    _dataDir = data;
                    return;
                }
                catch (Exception ex)
                {
                    _portable = false;
                    _notice = "portable data folder is not writable (" + ex.Message +
                              "); using " + appData;
                }
            }

            _dataDir = appData;
            if (!writable && !existing)
                _notice = "install folder is read-only; using " + appData;
            try { Directory.CreateDirectory(_dataDir); } catch { }
            if (migrate && !MigrateProfile(legacy, appData))
                _notice = "could not import the old " + legacy + " profile";
        }

        // Copies the old profile (left in place) into the new folder.
        internal static bool MigrateProfile(string oldDir, string newDir)
        {
            try
            {
                if (!Directory.Exists(oldDir)) return false;
                CopyDir(oldDir, newDir);
                return true;
            }
            catch { return false; }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); }
                catch { }
            }
            foreach (string d in Directory.GetDirectories(from))
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        // The only reliable writability check: access control can allow creating a
        // folder but still deny creating files in it.
        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".zid-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
