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
    //  COM bridge  (window.external.*)
    // =====================================================================
    // Every method here is called by page script. An exception that escapes
    // becomes an IE script error and can break the page, so each call is
    // guarded: log it, return a safe value, and let the UI keep running.
    [ComVisible(true)]
    public class ScriptBridge
    {
        private readonly MainForm _form;
        public ScriptBridge(MainForm form) { _form = form; }

        private static void Fail(string where, Exception ex)
        {
            try { Program.ReportCrash("external." + where, ex); } catch { }
        }

        private static T Fail<T>(string where, Exception ex)
        {
            Fail(where, ex);
            return default(T);
        }

        public void Add(string url, string fileName, string saveFolder, string cookie,
                        string parts, string speedKbps, string headers, string checksum,
                        string mirror, string proxy)
        {
            try
            {
                _form.BackendAdd(url, fileName, saveFolder, cookie, parts, speedKbps,
                                 headers, checksum, mirror, proxy);
            }
            catch (Exception ex) { Fail("Add", ex); }
        }
        public void Edit(int id, string url, string fileName, string saveFolder, string cookie,
                         string parts, string speedKbps, string headers, string checksum,
                         string mirror, string proxy, string startAt)
        {
            try
            {
                _form.BackendEdit(id, url, fileName, saveFolder, cookie, parts, speedKbps,
                                  headers, checksum, mirror, proxy, startAt);
            }
            catch (Exception ex) { Fail("Edit", ex); }
        }
        public string ImportUrls()
        {
            try { return _form.BackendImportUrls(); }
            catch (Exception ex) { return Fail<string>("ImportUrls", ex); }
        }

        public void Pause(int id)      { try { _form.BackendPause(id); } catch (Exception ex) { Fail("Pause", ex); } }
        public void Resume(int id)     { try { _form.BackendResume(id); } catch (Exception ex) { Fail("Resume", ex); } }
        public void Stop(int id)       { try { _form.BackendPause(id); } catch (Exception ex) { Fail("Stop", ex); } }
        public void RemoveItem(int id, int deleteFile) { try { _form.BackendRemoveItem(id, deleteFile); } catch (Exception ex) { Fail("RemoveItem", ex); } }
        public void ExportList()       { try { _form.BackendExportList(); } catch (Exception ex) { Fail("ExportList", ex); } }
        public void ClearCompleted(int removeOrphans) { try { _form.BackendClearCompleted(removeOrphans); } catch (Exception ex) { Fail("ClearCompleted", ex); } }
        public void ForgetCredentials() { try { _form.BackendForgetCredentials(); } catch (Exception ex) { Fail("ForgetCredentials", ex); } }
        public void SavePrefs(string packed) { try { _form.BackendSavePrefs(packed); } catch (Exception ex) { Fail("SavePrefs", ex); } }
        public string PickColor(string current)
        {
            try { return _form.BackendPickColor(current); }
            catch (Exception ex) { return Fail<string>("PickColor", ex); }
        }
        public void ProbeUrl(string url, int token) { try { _form.BackendProbeUrl(url, token); } catch (Exception ex) { Fail("ProbeUrl", ex); } }
        public void Restart(int id)    { try { _form.BackendRestart(id); } catch (Exception ex) { Fail("Restart", ex); } }
        public void RetrySingle(int id) { try { _form.BackendRetrySingle(id); } catch (Exception ex) { Fail("RetrySingle", ex); } }
        public void SetCredentials(int id) { try { _form.BackendSetCredentials(id); } catch (Exception ex) { Fail("SetCredentials", ex); } }
        public void RetryAll()         { try { _form.BackendRetryAll(); } catch (Exception ex) { Fail("RetryAll", ex); } }
        public void SetPriority(int id, int priority) { try { _form.BackendSetPriority(id, priority); } catch (Exception ex) { Fail("SetPriority", ex); } }
        public void StopAll()          { try { _form.BackendPauseAll(); } catch (Exception ex) { Fail("StopAll", ex); } }
        public void PauseAll()         { try { _form.BackendPauseAll(); } catch (Exception ex) { Fail("PauseAll", ex); } }
        public void ResumeAll()        { try { _form.BackendResumeAll(); } catch (Exception ex) { Fail("ResumeAll", ex); } }

        public void Open(int id)       { try { _form.BackendOpen(id); } catch (Exception ex) { Fail("Open", ex); } }
        public void OpenFolder(int id) { try { _form.BackendOpenFolder(id); } catch (Exception ex) { Fail("OpenFolder", ex); } }
        public void CopyUrl(int id)    { try { _form.BackendCopyUrl(id); } catch (Exception ex) { Fail("CopyUrl", ex); } }
        public void RefreshUrl(int id) { try { _form.BackendRefreshUrl(id); } catch (Exception ex) { Fail("RefreshUrl", ex); } }
        public string GetItemInfo(int id)
        {
            try { return _form.BackendGetItemInfo(id); }
            catch (Exception ex) { return Fail<string>("GetItemInfo", ex); }
        }
        public string GetItemInfoSecrets(int id)
        {
            try { return _form.BackendGetItemInfo(id, true); }
            catch (Exception ex) { return Fail<string>("GetItemInfoSecrets", ex); }
        }
        public void About()            { try { _form.BackendAbout(); } catch (Exception ex) { Fail("About", ex); } }
        public void CheckUpdates()     { try { _form.BackendCheckUpdates(); } catch (Exception ex) { Fail("CheckUpdates", ex); } }
        public void OpenLog()          { try { _form.BackendOpenLog(); } catch (Exception ex) { Fail("OpenLog", ex); } }
        public void CopyDiagnostics()  { try { _form.BackendCopyDiagnostics(); } catch (Exception ex) { Fail("CopyDiagnostics", ex); } }
        public void ShowProgress(int id) { try { _form.BackendShowProgress(id); } catch (Exception ex) { Fail("ShowProgress", ex); } }
        public void HideProgress(int id) { try { _form.BackendHideProgress(id); } catch (Exception ex) { Fail("HideProgress", ex); } }
        public void SaveTheme(string theme) { try { _form.BackendSaveTheme(theme); } catch (Exception ex) { Fail("SaveTheme", ex); } }
        public void SaveSettings(string packed)
        {
            try
            {
                int mc = 2, pr = 1, spd = 0, clip = 0, shut = 0, auto = 0, daily = 0;
                int updateCheck = 1, updateDays = 7, sl = 0;
                string proxy = "", sched = "", lang = "", stop = "", capture = "", blocked = "";
                string naming = "", slFrom = "", slTo = "";
                if (!string.IsNullOrEmpty(packed))
                {
                    string[] parts = packed.Split('|');
                    if (parts.Length > 0) int.TryParse(parts[0], out mc);
                    if (parts.Length > 1) int.TryParse(parts[1], out pr);
                    if (parts.Length > 2) int.TryParse(parts[2], out spd);
                    if (parts.Length > 3) { try { proxy = Uri.UnescapeDataString(parts[3]); } catch { } }
                    if (parts.Length > 4) int.TryParse(parts[4], out clip);
                    if (parts.Length > 5) int.TryParse(parts[5], out shut);
                    if (parts.Length > 6) int.TryParse(parts[6], out auto);
                    if (parts.Length > 7) { try { sched = Uri.UnescapeDataString(parts[7]); } catch { } }
                    if (parts.Length > 8) { try { lang = Uri.UnescapeDataString(parts[8]); } catch { } }
                    if (parts.Length > 9) { try { stop = Uri.UnescapeDataString(parts[9]); } catch { } }
                    if (parts.Length > 10) int.TryParse(parts[10], out daily);
                    if (parts.Length > 11) { try { capture = Uri.UnescapeDataString(parts[11]); } catch { } }
                    if (parts.Length > 12) int.TryParse(parts[12], out updateCheck);
                    if (parts.Length > 13) int.TryParse(parts[13], out updateDays);
                    if (parts.Length > 14) { try { blocked = Uri.UnescapeDataString(parts[14]); } catch { } }
                    if (parts.Length > 15) { try { naming = Uri.UnescapeDataString(parts[15]); } catch { } }
                    if (parts.Length > 16) int.TryParse(parts[16], out sl);
                    if (parts.Length > 17) { try { slFrom = Uri.UnescapeDataString(parts[17]); } catch { } }
                    if (parts.Length > 18) { try { slTo = Uri.UnescapeDataString(parts[18]); } catch { } }
                }
                if (mc < 1) mc = 1; if (mc > 8) mc = 8;
                if (pr < 1) pr = 1; if (pr > 8) pr = 8;
                if (spd < 0) spd = 0;
                if (sl < 0) sl = 0;
                _form.BackendSaveSettings(mc, pr, spd, proxy, clip, shut, auto, sched, lang, stop, daily, capture,
                                          updateCheck, updateDays, blocked, naming, sl, slFrom, slTo);
            }
            catch (Exception ex) { Fail("SaveSettings", ex); }
        }
        public string GetSettings()
        {
            try { return _form.BackendGetSettings(); }
            catch (Exception ex) { return Fail<string>("GetSettings", ex); }
        }
        public void BrowserIntegration() { try { _form.BackendBrowserIntegration(); } catch (Exception ex) { Fail("BrowserIntegration", ex); } }
        public void ExitApp()          { try { Application.Exit(); } catch (Exception ex) { Fail("ExitApp", ex); } }

        public void BeginDrag()        { try { _form.BeginWindowDrag(); } catch (Exception ex) { Fail("BeginDrag", ex); } }
        public void MinimizeWindow()   { try { _form.WindowState = FormWindowState.Minimized; } catch (Exception ex) { Fail("MinimizeWindow", ex); } }
        public void ToggleMaximize()   { try { _form.ToggleWindowState(); } catch (Exception ex) { Fail("ToggleMaximize", ex); } }
        public void CloseWindow()      { try { _form.Close(); } catch (Exception ex) { Fail("CloseWindow", ex); } }

        // Called by ui.html's window.onerror so script faults reach app.log
        // instead of an IE error dialog the user has to dismiss.
        public void LogError(string message)
        {
            try { Logger.Error("ui: " + (message == null ? "" : message)); } catch { }
        }
    }
}
