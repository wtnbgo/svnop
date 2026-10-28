namespace SvnOp;

/// <summary>認証キャッシュの方針を選ぶ画面 (agent の初回起動時)。インストーラの選択ページと同じ内容</summary>
sealed class CacheModeForm : Form
{
    readonly RadioButton _keep = new() { AutoSize = true, Checked = true };
    readonly RadioButton _none = new() { AutoSize = true };
    readonly CheckBox _sync = new() { AutoSize = true, Checked = true, Margin = new Padding(24, 0, 0, 8) };

    public string Mode => _keep.Checked ? Config.Keep : Config.NoCache;
    public bool SyncOnStart => _sync.Checked;

    public CacheModeForm(Config current)
    {
        Text = L.T("svnop - 認証キャッシュの設定", "svnop - credential cache");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _keep.Text = L.T("保存する (おすすめ)", "Save (recommended)");
        _none.Text = L.T("保存しない", "Do not save");
        _sync.Text = L.T("ログオン時に 1Password からキャッシュを同期する", "Sync the cache from 1Password at logon");
        if (current.CacheModeChosen)
        {
            _keep.Checked = current.KeepCache;
            _none.Checked = !current.KeepCache;
            _sync.Checked = current.SyncOnStart;
        }
        _keep.CheckedChanged += (_, _) => _sync.Enabled = _keep.Checked;
        _sync.Enabled = _keep.Checked;

        Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(24, 0, 0, 8), ForeColor = SystemColors.GrayText };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        AcceptButton = ok;
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        layout.Controls.AddRange([
            new Label
            {
                Text = L.T("TortoiseSVN の認証キャッシュ (%APPDATA%\\Subversion) にパスワードを保存しますか?",
                           "Save passwords in TortoiseSVN's credential cache (%APPDATA%\\Subversion)?"),
                AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 10),
            },
            _keep,
            Note(L.T("TortoiseSVN も、TortoiseSVN のコマンドライン版 (svn.exe) も、キャッシュを使って認証します。"
                     + "キャッシュに無いときは svnop が認証ダイアログに自動入力し、そのまま保存します。"
                     + "パスワードは Windows の暗号化 (DPAPI) でディスクに残ります。",
                     "Both TortoiseSVN and its command-line svn.exe authenticate from the cache. "
                     + "When an entry is missing, svnop fills the authentication dialog and it is saved. "
                     + "Passwords stay on disk, encrypted by Windows (DPAPI).")),
            _sync,
            _none,
            Note(L.T("ディスクにパスワードを残しません。TortoiseSVN の認証ダイアログには毎回 svnop が自動入力します。"
                     + "コマンドライン版 (svn.exe) は自動入力できないため、毎回パスワードを聞かれます。",
                     "No passwords are left on disk. svnop fills TortoiseSVN's authentication dialog every time. "
                     + "The command-line svn.exe cannot be auto-filled, so it asks for the password every time.")),
            new Label
            {
                Text = L.T("msys2 の svn はどちらの場合も 1Password から直接受け取ります。あとでトレイメニューの「設定」から変更できます。",
                           "msys2 svn gets passwords from 1Password in either case. You can change this later from Settings in the tray menu."),
                AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 4, 0, 10),
            },
            ok,
        ]);
        Controls.Add(layout);
    }
}
