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
    //  Confirmation dialog for links pushed by the browser extension
    // =====================================================================
    internal sealed class NeatDownloadForm : Form
    {
        private readonly Dictionary<string, string> _lang;
        private readonly TextField _url, _name, _folder;
        private readonly string _defaultFolder;
        private readonly bool _dark;
        private readonly string _host;

        public bool StartNow = true;
        // Set when the user confirmed blocking this domain; the caller saves it
        // and leaves the browser's own copy alone.
        public bool BlockDomain;

        public string FileName { get { return _name.Text.Trim(); } }
        public string Folder   { get { return _folder.Text.Trim(); } }

        public NeatDownloadForm(NeatRequest req, string defaultFolder, bool autoSort,
                                Dictionary<string, string> lang, string theme)
        {
            _lang = lang ?? new Dictionary<string, string>();
            _defaultFolder = defaultFolder;
            _dark = (theme == "dark");
            _host = Util.HostOf(req.Url);

            string name = req.FileName;
            if (Util.IsBlank(name)) name = GuessName(req.Url);
            if (Util.IsBlank(name)) name = "download.bin";
            string ext = Path.GetExtension(name).TrimStart('.');
            string type = ext.Length == 0 ? "FILE" : ext.ToUpperInvariant();
            string folder = autoSort
                ? Path.Combine(defaultFolder, Aria.TypeFolder(type))
                : defaultFolder;

            Text = Lang.Get(_lang, "neat.title", "Add download");
            ClientSize = new Size(580, 340);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Theme.Prepare(this, _dark);
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            Theme.Header(this, _lang, _dark);
            Panel foot = Theme.Footer(this, _dark);

            Panel card = Theme.Card(14, Theme.HeaderHeight + 10, 552, 212, _dark);
            Controls.Add(card);

            Label lUrl = MakeLabel(Lang.Get(_lang, "neat.url", "URL:"), 16, 12);
            _url = MakeText(104, 6, 432);
            _url.ReadOnly = true;
            _url.Box.TabStop = false;
            _url.Text = req.Url;
            card.Controls.Add(lUrl);
            card.Controls.Add(_url);

            Label lName = MakeLabel(Lang.Get(_lang, "neat.name", "File name:"), 16, 56);
            _name = MakeText(104, 50, 432);
            _name.Text = name;
            card.Controls.Add(lName);
            card.Controls.Add(_name);

            Label lFolder = MakeLabel(Lang.Get(_lang, "neat.folder", "Save to:"), 16, 100);
            _folder = MakeText(104, 94, 322);
            _folder.Text = folder;
            Button browse = Theme.Button(Lang.Get(_lang, "neat.browse", "Browse..."), false, _dark);
            browse.SetBounds(434, 94, 102, 32);
            browse.Click += delegate { Browse(); };
            card.Controls.Add(lFolder);
            card.Controls.Add(_folder);
            card.Controls.Add(browse);

            Label lSize = MakeLabel(Lang.Get(_lang, "neat.size", "Size:"), 16, 150);
            Label size = Theme.Body(req.Size > 0 ? SizeText(req.Size)
                                                 : Lang.Get(_lang, "neat.unknown_size", "Unknown"), _dark);
            size.SetBounds(104, 150, 432, 20);
            card.Controls.Add(lSize);
            card.Controls.Add(size);

            CheckBox block = new CheckBox();
            block.Text = Lang.Get(_lang, "neat.block", "Never auto download from this domain");
            block.SetBounds(104, 174, 432, 24);
            block.ForeColor = Theme.Text(_dark);
            block.BackColor = Theme.Panel(_dark);
            block.Font = new Font("Segoe UI", 9f);
            block.Cursor = Cursors.Hand;
            block.CheckedChanged += delegate { ConfirmBlock(block); };
            card.Controls.Add(block);

            Button start = Theme.Button(Lang.Get(_lang, "neat.start", "Start"), true, _dark);
            start.SetBounds(258, 13, 96, 32);
            start.Click += delegate { StartNow = true; DialogResult = DialogResult.OK; Close(); };
            foot.Controls.Add(start);
            AcceptButton = start;

            Button later = Theme.Button(Lang.Get(_lang, "neat.later", "Start later"), false, _dark);
            later.SetBounds(362, 13, 104, 32);
            later.Click += delegate { StartNow = false; DialogResult = DialogResult.OK; Close(); };
            foot.Controls.Add(later);

            Button cancel = Theme.Button(Lang.Get(_lang, "btn.cancel", "Cancel"), false, _dark);
            cancel.SetBounds(474, 13, 88, 32);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            foot.Controls.Add(cancel);
            CancelButton = cancel;
            // Tab through the fields before the footer actions.
            foot.TabIndex = Controls.Count;
        }

        // The browser can still own the foreground when this dialog opens.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            MainForm.ForceForeground(Handle);
        }

        private void Browse()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = Lang.Get(_lang, "neat.folder", "Save to:");
            d.SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : _defaultFolder;
            if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
        }

        // Checking the box asks first; Yes blocks the domain and closes the
        // dialog, No leaves the checkbox unchecked so the user stays here.
        private void ConfirmBlock(CheckBox box)
        {
            if (!box.Checked || _host.Length == 0) return;
            string text = string.Format(
                Lang.Get(_lang, "neat.block_ask",
                         "Stop taking over downloads from {0}? They will stay in the browser."),
                _host);
            if (MsgBox.Confirm(this, text, Lang.Get(_lang, "neat.block_title", "Blocked websites"),
                               Lang.Get(_lang, "btn.yes", "Yes"), Lang.Get(_lang, "btn.no", "No"),
                               _lang, _dark) == DialogResult.OK)
            {
                BlockDomain = true;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            box.Checked = false;
        }


        private static string GuessName(string url)
        {
            try
            {
                string n = Path.GetFileName(new Uri(url).AbsolutePath);
                if (!string.IsNullOrEmpty(n)) return Uri.UnescapeDataString(n);
            }
            catch { }
            return "";
        }

        private static string SizeText(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.0") + " " + u[i];
        }

        private Label MakeLabel(string text, int x, int y)
        {
            Label l = Theme.Section(text, _dark);
            l.SetBounds(x, y + 7, 84, 18);
            return l;
        }

        private TextField MakeText(int x, int y, int w)
        {
            return Theme.TextField(x, y, w, _dark);
        }
    }
}
