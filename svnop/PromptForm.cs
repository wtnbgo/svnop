namespace SvnOp;

/// <summary>agent の入力ウインドウ。1Password に無い / 1Password の値で失敗したときに出す</summary>
sealed class PromptForm : Form
{
    readonly TextBox _user = new() { Width = 320 };
    readonly TextBox _pass = new() { Width = 320, UseSystemPasswordChar = true };
    readonly CheckBox _save = new() { Text = L.T("1Password に記録する", "Record in 1Password"), Checked = true, AutoSize = true };

    public string User => _user.Text;
    public string Password => _pass.Text;
    public bool Save => _save.Checked;

    public PromptForm(string realm, string message, string? user)
    {
        Text = L.T("svnop - Subversion 認証", "svnop - Subversion authentication");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("キャンセル", "Cancel"), DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        _user.Text = user ?? "";

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(Control left, Control? right = null)
        {
            if (right == null)
            {
                layout.Controls.Add(left);
                layout.SetColumnSpan(left, 2);
            }
            else
            {
                layout.Controls.Add(left);
                layout.Controls.Add(right);
            }
        }
        Row(new Label { Text = message, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DarkRed });
        Row(new Label { Text = realm, AutoSize = true, MaximumSize = new Size(440, 0) });
        Row(new Label { Text = L.T("ユーザー名", "User name"), AutoSize = true, Anchor = AnchorStyles.Left }, _user);
        Row(new Label { Text = L.T("パスワード", "Password"), AutoSize = true, Anchor = AnchorStyles.Left }, _pass);
        Row(_save);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Row(buttons);
        Controls.Add(layout);

        Shown += (_, _) =>
        {
            Activate();
            (_user.Text.Length > 0 ? _pass : _user).Focus();
        };
    }
}
