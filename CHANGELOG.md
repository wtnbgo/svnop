# Changelog

## [0.3.0] - 2026-10-29

**Download / ダウンロード**: `svnop-setup-0.3.0.exe` (installer / インストーラ) or `svnop-0.3.0-win-x64.zip`.

Security fixes from an audit / セキュリティ監査を受けた修正

Behavior changes / 動作の変更:
- `svnop realm` / `add` / `find` verify https certificates; add `--insecure` for servers with self-signed certificates
  / https の証明書を検証するようになった。自己署名証明書のサーバでは `--insecure` を付ける
- Recording a password from msys2 svn through the agent now asks for confirmation in the agent
  / msys2 の svn から agent 経由で 1Password に記録するとき、agent が確認を出す

- Named pipe: the client verifies that the server is an svnop executable of the same user in the same folder before sending
  or receiving passwords; the agent refuses a pre-existing pipe of the same name, denies network clients, accepts concurrent
  connections with size and time limits, and asks before recording (save). New setting `allowPipe`
  / 名前付きパイプ: クライアントはパスワードの送受信前に、相手が同じユーザー・同じフォルダの svnop であることを確認する。agent は先に作られた
  同名パイプを使わず、ネットワーク経由の接続を拒否し、同時接続と大きさ・時間の上限を設け、記録 (save) の前に確認する。設定 `allowPipe` を追加
- op.exe is used only with a valid AgileBits signature; configured paths must be absolute
  / op.exe は AgileBits の正しい署名があるものだけを使い、設定する場合は絶対パスに限る
- Auto-fill only for specific, validly signed TortoiseSVN executables; the dialog is re-checked right before filling
  / 自動入力は、署名された決まった TortoiseSVN の exe だけを対象にし、入力直前にダイアログを再確認する
- Realm probing verifies https certificates (`--insecure` for self-signed servers) and limits svn:// responses
  / realm の調査で https の証明書を検証する (自己署名は `--insecure`)。svn:// の応答に上限を設けた
- Cache: plain-text entries are re-encrypted; sync / lock / mode changes are serialized and an in-flight sync is cancelled by lock;
  `import --adopt` only adopts entries confirmed to match 1Password; lock only deletes files named after their realm;
  cache files and config.json are validated
  / キャッシュ: 平文を暗号化し直す。sync・lock・方針変更を排他し、lock で取得中の同期を取り消す。`import --adopt` は 1Password と
  一致を確認したものだけを管理下に置く。lock は realm から決まる名前のファイルだけを消す。キャッシュファイルと config.json を検証する
- setup msys2: paths are quoted for bash; broken markers stop the change; removal restores only the original pinentry line
  and does nothing when svnop was not set up
  / setup msys2: パスを bash 用に引用する。マーカーが壊れていれば変更しない。解除は元の pinentry 行だけを戻し、設定していなければ何もしない
- pinentry: relays Assuan INQUIRE, decodes data as bytes, joins multiple data lines
  / pinentry: INQUIRE を中継し、データをバイト単位で復号し、複数のデータ行をつなげる
- CI: actions pinned to commit SHAs, Inno Setup version pinned, write permission only for the release job, tag names passed via environment variables
  / CI: Actions をコミット SHA で固定、Inno Setup のバージョンを固定、書き込み権限は release ジョブだけ、タグ名は環境変数で渡す
- Native buffers with decrypted data are cleared; log lines escape control characters; op error output is truncated
  / 復号したネイティブのバッファを消去。ログの制御文字をエスケープ。op のエラー出力を切り詰める

## [0.2.1] - 2026-10-28

**Download / ダウンロード**: `svnop-setup-0.2.1.exe` (installer / インストーラ) or `svnop-0.2.1-win-x64.zip`.

- Fix: the setup run by the installer crashed and the agent did not start. The installer runs with RedirectionGuard,
  which blocks starting `op.exe` through the WinGet `Links` symlink (also inherited by the agent started from the installer);
  svnop now finds `op.exe` in the WinGet package folder directly
  / 修正: インストーラから実行した設定が落ち、agent が起動しなかった。インストーラは RedirectionGuard 付きで動き
  (インストーラから起動した agent にも引き継がれる)、WinGet の `Links` のシンボリックリンク経由では `op.exe` を起動できない。
  WinGet のパッケージフォルダにある `op.exe` を直接使うようにした
- The installer explains that 1Password asks for approval during setup
  / 設定の途中で 1Password の承認画面が出ることを、インストーラに説明として表示する
- svnop no longer crashes when op cannot be started or when there is no console
  / op を起動できないときや、コンソールが無いときでも svnop が落ちないようにした

## [0.2.0] - 2026-10-28

**Download / ダウンロード**: `svnop-setup-0.2.0.exe` (installer / インストーラ) or `svnop-0.2.0-win-x64.zip`.
When upgrading from 0.1.0, the agent asks for the credential cache mode once (or choose it in the installer).
/ 0.1.0 からの更新では、agent が一度だけ認証キャッシュの方針を聞きます (インストーラで選んだ場合は聞きません)。

- **Credential cache mode** (`cacheMode`: `keep` / `none`) replaces `saveAuth`. It is chosen in the installer
  (or at the agent's first start), and `syncOnStart` applies only with `keep`
  / **認証キャッシュの方針** (`cacheMode`: `keep` / `none`) を導入し `saveAuth` を廃止。インストーラ (または agent の初回起動時) に選び、
  `syncOnStart` は `keep` のときだけ有効
- Tray notification when an auth dialog is auto-filled (`notifyOnFill`)
  / 認証ダイアログに自動入力したときにトレイ通知を出す (`notifyOnFill`)
- Tray menu: Settings (cache mode, sync at logon, notify, auto-submit, language, open config file) and Help (README)
  / トレイメニューに「設定」(方針・ログオン時の同期・通知・OK の自動押下・表示言語・設定ファイル) と「ヘルプ (README)」を追加
- `svnop config` command, and `--cache` / `--sync-on-start` / `--language` options for `svnop setup tortoisesvn`
  / `svnop config` コマンドと、`svnop setup tortoisesvn` の `--cache` / `--sync-on-start` / `--language` を追加
- Display language can be set in config.json (`language`) and from the tray; the installer language is used as the initial value
  / 表示言語を config.json (`language`) とトレイから設定可能に。インストーラで選んだ言語を初期値にする
- `svnop sync` is refused in the "none" mode (unless `--force`) / 「保存しない」方針では `svnop sync` を実行しない (`--force` を除く)

## [0.1.0] - 2026-10-28

First release / 最初のリリース

**Download / ダウンロード**: `svnop-setup-0.1.0.exe` (installer / インストーラ) or `svnop-0.1.0-win-x64.zip`.
Requires the .NET 10 Desktop Runtime and the 1Password CLI. / .NET 10 デスクトップ ランタイムと 1Password CLI が必要です。

- TortoiseSVN: fills authentication dialogs from 1Password; asks and records when a realm is missing or the stored password fails
  / 認証ダイアログに 1Password の値を自動入力。未登録や認証失敗のときは入力ウインドウを出して 1Password に記録
- svn.exe (console): syncs the wincrypt cache in `%APPDATA%\Subversion` with 1Password (at logon, `sync` / `lock`), and records newly saved credentials
  / `%APPDATA%\Subversion` の wincrypt キャッシュを 1Password と同期 (ログオン時・`sync` / `lock`)。新しく保存された認証情報を記録
- msys2 svn: pinentry for gpg-agent backed by 1Password through the agent
  / gpg-agent の pinentry として agent 経由で 1Password を参照
- `realm`, `add`, `find`, `list`, `import`, `cache`, `status`, `gpg-init` commands
- `setup tortoisesvn | msys2` (with `--remove` / `--dry-run`)
- Japanese and English UI / 日本語・英語表示
- Per-user installer (Inno Setup) / ユーザ単位のインストーラ
