namespace SvnOp;

/// <summary>svnop help / svnop &lt;command&gt; --help の文面 (日英)</summary>
static class Help
{
    public static string Version =>
        typeof(Help).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Usage => L.T(
        $$"""
        svnop {{Version}} - Subversion の認証情報を 1Password で管理する

        使い方: svnop <コマンド> [オプション]
                svnop help <コマンド>    コマンドの詳しい説明

        初期設定
          setup tortoisesvn     TortoiseSVN / svn.exe 向けの設定 (ボールト指定・自動起動・常駐開始)
          setup msys2           msys2 の svn 向けの設定 (gpg-agent と pinentry)
          setup --remove ...    上記の設定を外す
          config [キー [値]]    設定を表示 / 変更する (認証キャッシュの方針・表示言語など)

        1Password への登録
          realm <URL>...        URL の realm (認証の単位) を調べる
          add <URL>             realm を調べて 1Password に登録 / 更新する
          find <URL|realm>      1Password の登録を確認する
          list                  1Password に登録済みの SVN アイテム一覧
          import                ローカルのキャッシュを 1Password に取り込む

        ローカルキャッシュ (TortoiseSVN / svn.exe が使う %APPDATA%\Subversion)
          sync                  1Password の内容をキャッシュに書き出す
          lock                  svnop が書いたキャッシュを消す
          status                svnop が管理しているキャッシュの状態
          cache [<URL>]         キャッシュにある realm の一覧

        常駐 (agent)
          agent                 常駐を開始する (認証ダイアログへの自動入力・キャッシュ監視・msys2 の窓口)
          agent --stop          常駐を終了する
          agent --status        常駐しているか確認する

        その他
          gpg-init              msys2 の svn 用キャッシュ (GPG-Agent 形式) を作る
          dialogs               表示中の TortoiseSVN ダイアログを調べる (調査用)
          call                  agent への中継 (pinentry が内部で使う)
          --version             バージョンを表示する

        共通オプション
          --auth-dir DIR        svn.simple の場所 (既定: %APPDATA%\Subversion\auth\svn.simple)

        設定ファイル: %APPDATA%\svnop\config.json   表示言語: svnop config language ja|en|auto (環境変数 SVNOP_LANG が優先)
        """,
        $$"""
        svnop {{Version}} - manage Subversion credentials with 1Password

        Usage: svnop <command> [options]
               svnop help <command>    detailed help for a command

        Setup
          setup tortoisesvn     set up for TortoiseSVN / svn.exe (vault, autostart, start the agent)
          setup msys2           set up for msys2 svn (gpg-agent and pinentry)
          setup --remove ...    undo the setup above
          config [key [value]]  show / change settings (credential cache mode, language, ...)

        Registering in 1Password
          realm <URL>...        find the realm (authentication scope) of a URL
          add <URL>             find the realm and add / update it in 1Password
          find <URL|realm>      check whether a realm is registered in 1Password
          list                  list SVN items registered in 1Password
          import                import passwords from the local cache into 1Password

        Local cache (%APPDATA%\Subversion, used by TortoiseSVN / svn.exe)
          sync                  write 1Password items into the cache
          lock                  delete the cache entries written by svnop
          status                show the state of the cache entries managed by svnop
          cache [<URL>]         list realms in the cache

        Agent (resident)
          agent                 start the agent (fills auth dialogs, watches the cache, serves msys2)
          agent --stop          stop the agent
          agent --status        check whether the agent is running

        Others
          gpg-init              create GPG-Agent style cache entries for msys2 svn
          dialogs               inspect TortoiseSVN dialogs on screen (diagnostics)
          call                  relay to the agent (used internally by the pinentry)
          --version             show the version

        Common options
          --auth-dir DIR        location of svn.simple (default: %APPDATA%\Subversion\auth\svn.simple)

        Config file: %APPDATA%\svnop\config.json   Language: svnop config language ja|en|auto (SVNOP_LANG overrides)
        """);

    static readonly Dictionary<string, (string Ja, string En)> Commands = new()
    {
        ["setup"] = (
            """
            svnop setup tortoisesvn [--vault V] [--cache keep|none] [--sync-on-start true|false]
                                    [--language auto|ja|en] [--no-autostart] [--dry-run]
            svnop setup msys2 [--msys-root DIR] [--dry-run]
            svnop setup --remove tortoisesvn|msys2 [--dry-run]

            tortoisesvn
              - 指定した設定を config.json に記録する (省略したものは今の設定のまま)
                  --vault          1Password のボールト
                  --cache          認証キャッシュにパスワードを保存するか
                                   keep: 保存する。TortoiseSVN もコマンドライン版 svn.exe もキャッシュで認証する
                                   none: 保存しない。TortoiseSVN は毎回自動入力、svn.exe は毎回パスワードを聞かれる
                                   (未選択でコンソールから実行した場合はその場で聞く。それ以外は agent の初回起動時に聞く)
                  --sync-on-start  keep のとき、ログオン時に 1Password からキャッシュを同期するか
                  --language       表示言語
              - 1Password CLI (op) が使えるか、ボールトがあるかを確かめる
              - ログオン時に agent (svnopw.exe) が起動するよう登録する (HKCU\...\Run)
              - agent を起動する
              続けて「svnop import --adopt」で既存のキャッシュを取り込んでください。

            msys2
              msys2 の svn (pacman -S subversion) を 1Password と連携させます。
              - ~/.gnupg/gpg-agent.conf の pinentry を svn-op-pinentry に差し替える (既存は .svnop-bak に退避)
              - ~/.config/svn-op/config.json を作る
              - ~/.bashrc に gpg-agent を起動する数行を足す (msys2 のシェルでだけ動く)
              - 1Password の各 realm について、~/.subversion にユーザ名だけのキャッシュを作る (gpg-init)
              --msys-root  msys2 のインストール先 (既定 C:\msys64)

            --remove  上記の変更を元に戻します (1Password とキャッシュの中身には触れません)
            --dry-run 変更内容を表示するだけで、何も変更しません
            """,
            """
            svnop setup tortoisesvn [--vault V] [--cache keep|none] [--sync-on-start true|false]
                                    [--language auto|ja|en] [--no-autostart] [--dry-run]
            svnop setup msys2 [--msys-root DIR] [--dry-run]
            svnop setup --remove tortoisesvn|msys2 [--dry-run]

            tortoisesvn
              - stores the given settings in config.json (omitted ones stay as they are)
                  --vault          the 1Password vault
                  --cache          whether to save passwords in the credential cache
                                   keep: save. Both TortoiseSVN and the command-line svn.exe authenticate from the cache
                                   none: do not save. TortoiseSVN is auto-filled every time; svn.exe asks every time
                                   (if not chosen yet, asked on the console when run interactively, otherwise by the agent at first start)
                  --sync-on-start  with keep, sync the cache from 1Password at logon
                  --language       display language
              - checks that the 1Password CLI (op) works and the vault exists
              - registers the agent (svnopw.exe) to start at logon (HKCU\...\Run)
              - starts the agent
              Then run "svnop import --adopt" to import existing cache entries.

            msys2
              Connects msys2 svn (pacman -S subversion) with 1Password.
              - points pinentry in ~/.gnupg/gpg-agent.conf to svn-op-pinentry (the old file is kept as .svnop-bak)
              - creates ~/.config/svn-op/config.json
              - adds a few lines to ~/.bashrc that start gpg-agent (only in msys2 shells)
              - creates username-only cache entries in ~/.subversion for each realm in 1Password (gpg-init)
              --msys-root  msys2 install directory (default C:\msys64)

            --remove  undoes the changes above (1Password and cache contents are left alone)
            --dry-run only prints what would change
            """),
        ["config"] = (
            """
            svnop config                 設定を一覧表示する
            svnop config <キー>          値を表示する
            svnop config <キー> <値>     値を変更する

            主なキー
              cacheMode     keep (認証キャッシュに保存する) / none (保存しない)
              syncOnStart   true / false   keep のとき、ログオン時に 1Password から同期する
              vault         1Password のボールト
              language      auto / ja / en   表示言語 (環境変数 SVNOP_LANG が優先)
              notifyOnFill  true / false   自動入力したら通知する
              autoSubmit    true / false   自動入力の後に OK を押す
            トレイメニューの「設定」からも変更できます。
            """,
            """
            svnop config                 list settings
            svnop config <key>           show a value
            svnop config <key> <value>   change a value

            Main keys
              cacheMode     keep (save in the credential cache) / none (do not save)
              syncOnStart   true / false   with keep, sync from 1Password at logon
              vault         the 1Password vault
              language      auto / ja / en   display language (SVNOP_LANG overrides)
              notifyOnFill  true / false   notify when auto-filled
              autoSubmit    true / false   press OK after auto-fill
            These can also be changed from Settings in the tray menu.
            """),
        ["realm"] = (
            """
            svnop realm <URL>...

            URL に認証なしでアクセスし、Subversion が使う realm 文字列を表示します。
            形式は「<scheme://host:port> 名前」で、http(s) は Basic / Digest 認証の realm、
            svn:// は svnserve の realm (未設定ならリポジトリの UUID) です。
            """,
            """
            svnop realm <URL>...

            Accesses the URL without credentials and prints the realm string Subversion uses.
            The format is "<scheme://host:port> name": the Basic / Digest realm for http(s),
            and the svnserve realm (the repository UUID if not configured) for svn://.
            """),
        ["add"] = (
            """
            svnop add <URL> [--user U] [--realm R] [--no-cache]

            realm を調べ、ユーザー名とパスワードを入力して 1Password に登録します。
            登録済みなら更新するか確認します。登録後、TortoiseSVN の svn.exe からもすぐ使えるよう
            キャッシュにも書き込みます (--no-cache で書かない)。
              --user   ユーザー名 (省略時はキャッシュの値か入力)
              --realm  realm を調べずに直接指定する
            """,
            """
            svnop add <URL> [--user U] [--realm R] [--no-cache]

            Finds the realm, asks for the user name and password, and adds them to 1Password.
            If already registered, asks whether to update. Afterwards the entry is also written to the
            cache so TortoiseSVN's svn.exe can use it right away (--no-cache to skip).
              --user   user name (default: from the cache, or asked)
              --realm  specify the realm directly instead of probing
            """),
        ["find"] = (
            "svnop find <URL|realm> [--user U]\n\n1Password に登録があるか確認します。パスワードは表示しません。",
            "svnop find <URL|realm> [--user U]\n\nChecks whether the realm is registered in 1Password. Passwords are never printed."),
        ["list"] = (
            "svnop list\n\n1Password のタグ svn のアイテムを realm / ユーザー名 / id で一覧表示します。",
            "svnop list\n\nLists 1Password items tagged svn as realm / user name / id."),
        ["import"] = (
            """
            svnop import [--all | --realm R] [--update] [--adopt] [--dry-run]

            ローカルのキャッシュ (wincrypt / 平文) を復号して一覧にし、選んだものを 1Password に登録します。
              --all      全件を取り込む (番号を聞かない)
              --realm    指定の realm だけ取り込む
              --update   登録済みでも値が違えば更新する
              --adopt    取り込んだキャッシュを svnop の管理下に置く (lock で消える)
              --dry-run  一覧を表示するだけ
            """,
            """
            svnop import [--all | --realm R] [--update] [--adopt] [--dry-run]

            Decrypts the local cache (wincrypt / plain text), lists the entries and adds the selected ones to 1Password.
              --all      import everything (no selection prompt)
              --realm    import only the given realm
              --update   update items that are already registered if the value differs
              --adopt    put the imported cache files under svnop management (deleted by lock)
              --dry-run  only list
            """),
        ["sync"] = (
            """
            svnop sync [--force] [--dry-run]

            1Password のタグ svn のアイテムを、TortoiseSVN / svn.exe のキャッシュ (wincrypt 形式) に書き出します。
            1Password と同じ内容のものは書き換えません。svnop が書いた後に変更されたものや、
            svnop の管理外で内容の違うものは --force を付けない限り触りません。
            認証キャッシュを保存しない設定 (cacheMode none) のときは実行しません (--force で実行)。
            保存する設定で syncOnStart が有効なら、agent が起動時 (ログオン時) に自動で sync します。
            """,
            """
            svnop sync [--force] [--dry-run]

            Writes 1Password items tagged svn into the TortoiseSVN / svn.exe cache (wincrypt format).
            Entries identical to 1Password are left as they are. Entries changed after svnop wrote them,
            or unmanaged entries with different values, are not touched unless --force is given.
            Refused when the cache mode is "none" (use --force to run anyway).
            With cache mode "keep" and syncOnStart, the agent syncs automatically when it starts (at logon).
            """),
        ["lock"] = (
            "svnop lock [--force]\n\nsvnop が書いた (管理下の) キャッシュを消します。書いた後に変更されたものは --force を付けない限り残します。\n戻すときは svnop sync。",
            "svnop lock [--force]\n\nDeletes the cache entries written (managed) by svnop. Entries changed afterwards are kept unless --force is given.\nUse svnop sync to restore them."),
        ["status"] = (
            "svnop status\n\nsvnop が管理しているキャッシュの状態 (同期済み / 変更あり / なし) を表示します。",
            "svnop status\n\nShows the state of the cache entries managed by svnop (synced / changed / missing)."),
        ["cache"] = (
            "svnop cache [<URL>] [--auth-dir DIR]\n\nキャッシュにある realm / ユーザー名 / 形式を表示します。URL を付けるとそのホストだけ。",
            "svnop cache [<URL>] [--auth-dir DIR]\n\nLists realm / user name / format of cache entries. With a URL, only that host."),
        ["agent"] = (
            """
            svnop agent [--stop | --status]

            常駐プログラム (svnopw.exe) を起動します。常駐中は次のことをします。
              - TortoiseSVN の認証ダイアログに 1Password の値を入れて OK を押す
              - 1Password に無い / 1Password の値で失敗したときは入力ウインドウを出し、1Password に記録する
              - svn.exe などがキャッシュに保存した新しい認証情報を、確認のうえ 1Password に記録する
              - 認証キャッシュを保存する設定なら、起動時に 1Password → キャッシュを同期する (syncOnStart)
              - 初回起動時、認証キャッシュの方針が未選択なら選択画面を出す
              - msys2 の pinentry からの問い合わせに答える (1Password の承認が 10 分に 1 回程度で済む)
            トレイアイコンのメニューから sync / lock / 設定 / ヘルプ (README) / ログ表示 / 終了ができます。
            """,
            """
            svnop agent [--stop | --status]

            Starts the resident program (svnopw.exe). While running it:
              - fills TortoiseSVN auth dialogs with values from 1Password and presses OK
              - shows its own input window when 1Password has no entry or the stored value fails, and records the input
              - records new credentials that svn.exe etc. saved to the cache into 1Password (after asking)
              - syncs 1Password to the cache at startup when the cache mode is "keep" (syncOnStart)
              - asks for the credential cache mode at first start if it has not been chosen
              - answers the msys2 pinentry (1Password approval is needed only about once per 10 minutes)
            The tray icon menu offers sync / lock / settings / help (README) / show log / exit.
            """),
        ["gpg-init"] = (
            "svnop gpg-init --auth-dir DIR [--dry-run]\n\n1Password の各 realm について、msys2 の svn 用にユーザー名だけのキャッシュ (passtype=gpg-agent) を作ります。\n既存のファイルには触れません。通常は svnop setup msys2 が実行します。",
            "svnop gpg-init --auth-dir DIR [--dry-run]\n\nCreates username-only cache entries (passtype=gpg-agent) for msys2 svn for each realm in 1Password.\nExisting files are left alone. Normally run by svnop setup msys2."),
        ["dialogs"] = (
            "svnop dialogs\n\n表示中の TortoiseSVN のダイアログと子コントロールを一覧表示します (調査用。パスワード欄の中身は出しません)。",
            "svnop dialogs\n\nLists TortoiseSVN dialogs on screen and their controls (diagnostics; password fields are never printed)."),
        ["call"] = (
            "svnop call\n\n標準入力の 1 行 JSON を agent に中継し、応答を 1 行で返します。agent が動いていなければ終了コード 3。\nmsys2 の svn-op-pinentry が内部で使います。",
            "svnop call\n\nRelays one line of JSON from stdin to the agent and prints the reply. Exit code 3 if the agent is not running.\nUsed internally by the msys2 svn-op-pinentry."),
    };

    /// <summary>コマンドの詳しい説明。不明なコマンドなら null</summary>
    public static string? For(string command) =>
        Commands.TryGetValue(command, out var t) ? L.T(t.Ja, t.En) : null;
}
