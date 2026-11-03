using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ZID
{
    // Small modal prompt for HTTP 401 / proxy 407 credentials. The password is
    // runtime-only on the item, never saved to the state file.
    internal sealed class CredentialsForm : Form
    {
        private readonly TextField _user, _pass;
        private readonly CheckBox _remember;
        private readonly bool _dark;

        public string UserText { get { return _user.Text.Trim(); } }
        public string PassText { get { return _pass.Text; } }
        public bool Remember { get { return _remember.Checked && _remember.Visible; } }

        public CredentialsForm(string url, string user, string pass, bool remember,
                               bool allowRemember, Dictionary<string, string> lang, bool dark)
        {
            _dark = dark;
            Text = Lang.Get(lang, "auth.title", "Credentials required");
            ClientSize = new Size(460, 310);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Theme.Prepare(this, dark);
            Theme.Header(this, lang, dark);
            Panel foot = Theme.Footer(this, dark);

            Panel card = Theme.Card(14, Theme.HeaderHeight + 10, 432, 190, dark);
            Controls.Add(card);

            Label msg = Theme.Body(Lang.Get(lang, "auth.message",
                "This server needs a user name and password."), dark);
            msg.SetBounds(16, 10, 400, 34);
            card.Controls.Add(msg);

            Label lUser = Theme.Section(Lang.Get(lang, "auth.user", "User name:"), dark);
            lUser.SetBounds(16, 56, 92, 18);
            _user = Theme.TextField(116, 48, 300, dark);
            _user.Text = user ?? "";
            card.Controls.Add(lUser);
            card.Controls.Add(_user);

            Label lPass = Theme.Section(Lang.Get(lang, "auth.pass", "Password:"), dark);
            lPass.SetBounds(16, 100, 92, 18);
            _pass = Theme.TextField(116, 92, 300, dark);
            _pass.Box.UseSystemPasswordChar = true;
            _pass.Text = pass ?? "";
            card.Controls.Add(lPass);
            card.Controls.Add(_pass);

            _remember = new CheckBox();
            _remember.Text = Lang.Get(lang, "auth.remember", "Remember for this site");
            _remember.SetBounds(116, 132, 300, 24);
            _remember.ForeColor = Theme.Text(dark);
            _remember.BackColor = Theme.Panel(dark);
            _remember.Font = new Font("Segoe UI", 9f);
            _remember.Cursor = Cursors.Hand;
            _remember.Checked = remember;
            _remember.Visible = allowRemember;
            card.Controls.Add(_remember);

            Button ok = Theme.Button(Lang.Get(lang, "btn.ok", "OK"), true, dark);
            ok.SetBounds(230, 13, 96, 32);
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            foot.Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = Theme.Button(Lang.Get(lang, "btn.cancel", "Cancel"), false, dark);
            cancel.SetBounds(334, 13, 96, 32);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            foot.Controls.Add(cancel);
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            MainForm.ForceForeground(Handle);
            _user.Box.Focus();
        }
    }
}
