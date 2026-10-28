# Changelog

## [Unreleased]

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
