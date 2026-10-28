# svnop — Subversion の認証情報を 1Password で管理する

[English](README.en.md)

svnop は、TortoiseSVN や Subversion のコマンドラインが使うパスワードを 1Password で一元管理するための Windows 用ツールです。

- **TortoiseSVN**: 認証ダイアログが出たら、1Password の値を自動で入力して OK を押します。
- **TortoiseSVN の svn.exe（コンソール）**: TortoiseSVN と共通のパスワードキャッシュ（`%APPDATA%\Subversion`）を 1Password と同期します。
- **msys2 の svn**: gpg-agent の pinentry として、1Password から値を渡します。
- **記録**: 新しいリポジトリでパスワードを入力したときや、パスワードを変更したときは、確認のうえ 1Password に記録します。

1Password 側では「Login」アイテム（タグ `svn`）として保存します。どのクライアントからも同じアイテムを使います。

---

## 目次

1. [必要なもの](#必要なもの)
2. [インストール](#インストール)
3. [初期設定（TortoiseSVN 向け）](#初期設定tortoisesvn-向け)
4. [初期設定（msys2 向け）](#初期設定msys2-向け)
5. [日常の使い方](#日常の使い方)
6. [コマンド一覧](#コマンド一覧)
7. [設定ファイル](#設定ファイル)
8. [仕組み](#仕組み)
9. [セキュリティについて](#セキュリティについて)
10. [困ったとき](#困ったとき)
11. [アンインストール](#アンインストール)
12. [ビルド](#ビルド)

---

## 必要なもの

| 必要なもの | 備考 |
|---|---|
| Windows 10 / 11 | |
| [.NET 10 デスクトップ ランタイム](https://dotnet.microsoft.com/download/dotnet/10.0) | インストーラが有無を確認します |
| 1Password デスクトップアプリ | 設定 → 開発者 →「1Password CLI と連携」を有効にする |
| [1Password CLI](https://developer.1password.com/docs/cli/get-started/)（`op`） | `winget install AgileBits.1Password.CLI` |
| TortoiseSVN | TortoiseSVN 向けに使う場合 |
| msys2 と `pacman -S subversion` | msys2 の svn 向けに使う場合（任意） |

## インストール

[Releases](https://github.com/wtnbgo/svnop/releases) から `svnop-setup-<バージョン>.exe` をダウンロードして実行します。管理者権限は要りません。ユーザーごとに `%LOCALAPPDATA%\Programs\svnop` へインストールされます。

インストール中に次の項目を選べます。

| 項目 | 内容 |
|---|---|
| TortoiseSVN 向けに設定する | 1Password のボールト名を記録し、ログオン時に agent が起動するよう登録して、agent を起動する（`svnop setup tortoisesvn`） |
| msys2 の svn 向けに設定する | msys2（`C:\msys64`）が見つかったときだけ表示されます（`svnop setup msys2`） |
| PATH に追加する | コマンドプロンプトや PowerShell から `svnop` を呼べるようにする |

インストーラから実行した設定の結果は `%APPDATA%\svnop\setup.log` に記録されます。

インストーラを使わない場合は、`svnop-<バージョン>-win-x64.zip` を任意のフォルダに展開し、`svnop setup tortoisesvn` を実行してください。

## 初期設定（TortoiseSVN 向け）

インストーラで「TortoiseSVN 向けに設定する」を選んだ場合は、1 は済んでいます。

1. **設定**

   ```
   svnop setup tortoisesvn --vault SVN
   ```

   - `--vault`: SVN の認証情報を入れる 1Password のボールト（事前に作っておく）。
     専用のボールトを作るのがおすすめです。省略すると op の既定のボールトを使います。
   - ログオン時に agent（`svnopw.exe`）が起動するよう登録し、agent を起動します。

2. **既存のパスワードを 1Password に取り込む**（TortoiseSVN に保存済みのパスワードがある場合）

   ```
   svnop import --dry-run      # 取り込める一覧を見る
   svnop import --adopt        # 番号を選んで取り込む（all で全件）
   ```

   `--adopt` を付けると、取り込んだキャッシュは svnop の管理下に入ります（`svnop lock` で消せるようになります）。

3. **1Password からキャッシュを作る**（別の PC で 1Password に登録済みの場合など）

   ```
   svnop sync
   ```

以後は、agent がログオン時に自動で `sync` します。

## 初期設定（msys2 向け）

msys2 の svn（`pacman -S subversion` で入れたもの）は Windows の暗号化キャッシュを使えません。そこで gpg-agent の pinentry 経由で 1Password を使います。

1. msys2 に subversion を入れます。

   ```bash
   pacman -S subversion
   ```

2. 設定します（Windows のコマンドプロンプトなどから実行）。

   ```
   svnop setup msys2
   ```

   次のことを行います。`svnop setup --remove msys2` で元に戻せます。

   | 対象 | 内容 |
   |---|---|
   | `~/.gnupg/gpg-agent.conf` | pinentry を `svn-op-pinentry.py` にする。元のファイルは `.svnop-bak` に退避する |
   | `~/.config/svn-op/config.json` | pinentry の設定を作る |
   | `~/.bashrc` | msys2 のシェルで gpg-agent を起動する数行を、`# >>> svnop >>>` で囲んで足す |
   | `~/.subversion/auth/svn.simple` | 1Password の各 realm について、ユーザー名だけを入れたキャッシュを作る。これで初回から 1Password を使う |

3. msys2 のシェルを開き直して、`svn` を使います。パスワードは agent 経由で 1Password から渡ります。

> msys2 に subversion を入れていない場合、msys2 のシェルの `svn` は PATH 上の TortoiseSVN の `svn.exe` になります。その場合は TortoiseSVN 向けの設定（キャッシュの同期）だけで動きます。

## 日常の使い方

| 場面 | 何が起きるか |
|---|---|
| TortoiseSVN の認証ダイアログが出た | agent が 1Password の値を入れて OK を押します |
| 1Password に登録のないリポジトリ | agent が入力ウインドウを出します。入力した値は 1Password とキャッシュに記録されます |
| サーバ側でパスワードが変わった | 自動入力の直後に同じダイアログが再び出ると、agent は失敗と判断して入力ウインドウを出します。新しい値で 1Password を更新します |
| svn.exe（コンソール）で新しいパスワードを入力して保存した | agent がキャッシュの変化に気付き、確認のうえ 1Password に記録します |
| msys2 の svn | gpg-agent → pinentry → agent → 1Password の順で値が渡ります。gpg-agent が 1 時間キャッシュします |
| 新しいリポジトリを事前に登録したい | `svnop add <URL>` |
| ローカルのキャッシュを消したい | `svnop lock`（戻すときは `svnop sync`）、またはトレイメニュー |

トレイアイコン（盾のアイコン）のメニューから、次の操作ができます。

- 1Password から同期（sync）
- キャッシュを消す（lock）
- ログを開く
- 終了

### 1Password の承認について

1Password は CLI からのアクセスを「プロセスごと」に承認します。承認は 10 分間操作がなければ失効し、最長でも 12 時間で失効します。
svnop では agent がまとめて 1Password にアクセスするので、承認ダイアログは通常「しばらく使っていなかった後の最初の 1 回」だけ出ます。

`svnop list` などのコマンドを直接実行したときは、そのコマンドのプロセスとして 1 回承認が必要です。

## コマンド一覧

詳しくは `svnop help <コマンド>` を見てください。

| コマンド | 内容 |
|---|---|
| `setup tortoisesvn \| msys2` | 初期設定（`--remove` で解除、`--dry-run` で確認だけ） |
| `realm <URL>` | URL の realm（認証の単位）を調べる |
| `add <URL>` | 1Password に登録する（キャッシュにも書く） |
| `find <URL\|realm>` | 1Password に登録があるか確認する |
| `list` | 登録済みの一覧を表示する |
| `import` | ローカルのキャッシュを 1Password に取り込む |
| `sync` / `lock` / `status` | キャッシュを同期する / 消す / 状態を見る |
| `cache [<URL>]` | キャッシュの realm 一覧を表示する |
| `agent [--stop \| --status]` | agent を起動する / 終了する / 状態を確認する |
| `gpg-init` | msys2 の svn 用にユーザー名だけのキャッシュを作る |
| `dialogs` | 画面上の TortoiseSVN ダイアログを調べる（調査用） |
| `--version` | バージョンを表示する |

表示言語は Windows の表示言語に従います。環境変数 `SVNOP_LANG=ja` / `en` で切り替えられます。

## 設定ファイル

`%APPDATA%\svnop\config.json`

```json
{
  "vault": "SVN",
  "op": "",
  "tag": "svn",
  "realmField": "svn_realm",
  "autoSubmit": true,
  "saveAuth": false,
  "syncOnStart": true
}
```

| キー | 既定値 | 内容 |
|---|---|---|
| `vault` | （op の既定） | 使う 1Password のボールト |
| `op` | （自動検出） | `op.exe` の場所 |
| `tag` | `svn` | 対象アイテムに付けるタグ |
| `realmField` | `svn_realm` | realm を入れるフィールドの名前 |
| `autoSubmit` | `true` | 自動入力の後に OK を押す |
| `saveAuth` | `false` | TortoiseSVN の「認証を保存」を残す（`false` なら外して送る） |
| `syncOnStart` | `true` | agent の起動時に 1Password からキャッシュへ同期する |

同じフォルダにある次のファイルは、svnop が自動で書き込みます。

| ファイル | 内容 |
|---|---|
| `managed.json` | svnop が書いたキャッシュの記録 |
| `realm-map.json` | realm から 1Password のアイテム ID への対応表。秘密情報は入らない |
| `agent.log` / `setup.log` | ログ。パスワードは書かない |

msys2 側の設定は `~/.config/svn-op/config.json` にあります（`svnop setup msys2` が作ります）。

## 仕組み

### realm

Subversion は認証情報を **realm** 単位で保存します。realm は `<scheme://host:port> 名前` の形の文字列です。

| 例 | 内容 |
|---|---|
| `<https://svn.example.com:443> VisualSVN Server` | http(s): Basic / Digest 認証の realm |
| `<svn://svn.example.com:3690> 0f1e2d3c-…` | svn://: svnserve の realm（未設定ならリポジトリの UUID） |

`svnop realm <URL>` は、認証情報を送らずにこの文字列を調べます。

### 1Password のアイテム

| 項目 | 値 |
|---|---|
| カテゴリ | Login |
| タグ | `svn` |
| タイトル | `SVN <ユーザー名> @ <realm>` |
| フィールド | `username` / `password` / `svn_realm` |

1 realm につき 1 アイテムです。タイトルを変えても、`svn_realm` フィールドで照合します。

### 構成

```
TortoiseSVN ──(認証ダイアログ)──▶ svnop agent ──▶ op ──▶ 1Password
svn.exe     ──(%APPDATA%\Subversion の wincrypt キャッシュ)◀── sync / 監視 ── svnop agent
msys2 svn   ──▶ gpg-agent ──▶ svn-op-pinentry ──▶ svnop call ──(名前付きパイプ)──▶ svnop agent
```

- agent は TortoiseSVN の exe が出した認証ダイアログだけを対象にします。インストール先のパスで確認するので、ほかのプログラムが似たダイアログを出しても入力しません。
- キャッシュは Subversion 標準の Windows 暗号化（DPAPI、ユーザー単位）で書きます。
- 名前付きパイプ（`\\.\pipe\svnop-<ユーザー名>`）には、同じユーザーしか接続できません。

## セキュリティについて

- **パスワードの扱い**: svnop はパスワードをログやコマンドラインに出しません。1Password への書き込みは、JSON を標準入力で渡しています。
- **キャッシュの中身**: `sync` 後のキャッシュは、TortoiseSVN が自分で保存する場合と同じ形式・同じ強度です。持ち出す PC でキャッシュを残したくない場合は、作業後に `svnop lock` を実行してください。
- **agent の窓口**: agent は、1Password の承認が有効な間、同じユーザーのプロセスからの問い合わせに応えます。gpg-agent のキャッシュと同程度の信頼モデルです。
- **専用ボールト**: SVN 用のボールトを分けると、op に渡す範囲を絞れます。チームでの共有にも使えます。

## 困ったとき

| 症状 | 対処 |
|---|---|
| 承認ダイアログが何度も出る | agent が動いているか確認する（`svnop agent --status`）。コマンドを直接実行したときは毎回 1 回出ます |
| `op.exe が見つかりません` | 1Password CLI を入れる。または `config.json` の `op` に場所を書く |
| `account is not signed in` | 1Password アプリで「1Password CLI と連携」を有効にし、アプリを解錠する |
| TortoiseSVN で自動入力されない | `svnop dialogs` でダイアログが認識されているか確認する。`%APPDATA%\svnop\agent.log` を見る |
| msys2 の svn が端末でパスワードを聞く | `~/.subversion/auth/svn.simple` にその realm のファイルがあるか確認する（なければ `svnop gpg-init --auth-dir …`）。gpg-agent が起動しているか（`gpgconf --launch gpg-agent`）確認する |
| msys2 のスクリプトから svn を使うと失敗する | 標準入力が端末でないと svn は非対話モードになり、pinentry を呼びません。`--force-interactive` を付けてください |
| msys2 の svn で証明書の確認が出る | msys2 の svn は TortoiseSVN とは別に証明書の受け入れを記録します。一度、端末で受け入れてください |

## アンインストール

「アプリと機能」から svnop をアンインストールします。次のことが自動で行われます。

- agent を終了する
- 自動起動の登録を削除する
- msys2 の設定を元に戻す（設定していた場合）

1Password のアイテムとローカルのキャッシュは残ります。キャッシュも消したい場合は、アンインストールの前に `svnop lock` を実行してください。

## ビルド

```
installer\build.ps1
```

.NET 10 SDK と Inno Setup 6 以降が必要です。`dist\` に次のものができます。

| 出力 | 内容 |
|---|---|
| `app\` | 実行ファイル一式 |
| `svnop-setup-<バージョン>.exe` | インストーラ |
| `svnop-<バージョン>-win-x64.zip` | 実行ファイル一式の zip |

リリースは GitHub Actions で作ります。`svnop/Directory.Build.props` のバージョンと `CHANGELOG.md` を更新してから、タグを push してください。

```
git tag v0.1.0
git push origin v0.1.0
```

ビルドが終わると、インストーラと zip を添付した Release ができます。リリースノートは `CHANGELOG.md` の該当バージョンの節から作られます。

| ディレクトリ | 内容 |
|---|---|
| `svnop/` | 本体（C#）。`svnop.exe`（コマンド）と `agentw/`（常駐用の `svnopw.exe`） |
| `msys2/svn-op-pinentry.py` | msys2 用の pinentry |
| `installer/` | Inno Setup スクリプトとビルドスクリプト |
| `.github/workflows/` | GitHub Actions（ビルドとリリース） |
| `docs/svn-1password-feasibility.md` | 調査記録 |
| `docs/tortoisesvn-tool-plan.md` | 設計と検証の記録 |

## ライセンス

[MIT](LICENSE)
