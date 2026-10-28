# Changelog

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
