# svnop — Subversion の認証情報を 1Password で管理する

[English](README.en.md)

svnop は、TortoiseSVN や Subversion のコマンドラインが使うパスワードを 1Password で一元管理するための Windows 用ツールです。

- 1Password の「Login」アイテム（タグ `svn`）を正本にします。どのクライアントからも同じアイテムを使います。
- 常駐プログラム（agent）が、TortoiseSVN の認証ダイアログに 1Password の値を自動で入力します。
- 新しいリポジトリのパスワードや変更後のパスワードは、確認のうえ 1Password に記録します。

---

## 目次

1. [考え方: 認証キャッシュの方針](#考え方-認証キャッシュの方針)
2. [必要なもの](#必要なもの)
3. [インストール](#インストール)
4. [初期設定（TortoiseSVN）](#初期設定tortoisesvn)
5. [初期設定（msys2 の svn）](#初期設定msys2-の-svn)
6. [日常の使い方](#日常の使い方)
7. [設定](#設定)
8. [コマンド一覧](#コマンド一覧)
9. [仕組み](#仕組み)
10. [セキュリティについて](#セキュリティについて)
11. [困ったとき](#困ったとき)
12. [アンインストール](#アンインストール)
13. [ビルドとリリース](#ビルドとリリース)

---

## 考え方: 認証キャッシュの方針

TortoiseSVN とそのコマンドライン版（`svn.exe`）は、パスワードを **認証キャッシュ**（`%APPDATA%\Subversion`）に保存して使い回します。
svnop では最初に、このキャッシュにパスワードを **保存するか** を決めます。

| | 保存する（`keep`、おすすめ） | 保存しない（`none`） |
|---|---|---|
| TortoiseSVN（GUI） | キャッシュで認証する。キャッシュに無いときは svnop が認証ダイアログに自動入力し、そのまま保存される | 毎回、svnop が認証ダイアログに自動入力する |
| TortoiseSVN の `svn.exe`（コマンドライン） | キャッシュで認証する | **毎回パスワードを聞かれる**（コンソールの入力には自動入力できないため） |
| msys2 の svn | 1Password から直接受け取る | 1Password から直接受け取る |
| ディスク上のパスワード | あり（Windows の DPAPI で暗号化） | なし |

**`svn.exe` をパスワード入力なしで使いたい場合は「保存する」を選んでください。**

「保存する」の場合は、続けて **ログオン時に 1Password からキャッシュを同期するか**（`syncOnStart`）を決めます。
有効にすると、1Password で変更したパスワードが次のログオンで自動的にキャッシュへ反映されます。

方針はインストール時に選びます。インストーラを使わなかった場合は、agent の初回起動時に選択画面が出ます。
後からトレイメニューの「設定」や `svnop config` で変更できます。

## 必要なもの

| 必要なもの | 備考 |
|---|---|
| Windows 10 / 11 | |
| [.NET 10 デスクトップ ランタイム](https://dotnet.microsoft.com/download/dotnet/10.0) | インストーラが有無を確認します |
| 1Password デスクトップアプリ | 設定 → 開発者 →「1Password CLI と連携」を有効にする |
| [1Password CLI](https://developer.1password.com/docs/cli/get-started/)（`op`） | `winget install AgileBits.1Password.CLI` |
| TortoiseSVN | |
| msys2 と `pacman -S subversion` | msys2 の svn を使う場合（任意） |

## インストール

[Releases](https://github.com/wtnbgo/svnop/releases) から `svnop-setup-<バージョン>.exe` をダウンロードして実行します。
管理者権限は要りません。ユーザーごとに `%LOCALAPPDATA%\Programs\svnop` へインストールされます。

インストーラでは次のことを選びます。

| 画面 | 内容 |
|---|---|
| 言語 | インストーラと svnop の表示言語（日本語 / English） |
| タスク | 「TortoiseSVN 向けに設定する」「msys2 の svn 向けに設定する」（msys2 があるときだけ）「PATH に追加する」 |
| 1Password のボールト | SVN の認証情報を入れるボールト（専用のボールトを作っておくのがおすすめ） |
| 認証キャッシュ | [保存するかどうか](#考え方-認証キャッシュの方針) |
| ログオン時の同期 | 保存する場合だけ表示されます |

「TortoiseSVN 向けに設定する」を選ぶと、ログオン時に agent が起動するよう登録し、agent を起動します。
設定の途中で 1Password の承認画面が 1〜2 回出ます（ボールトの確認と、agent の最初の同期のため）。承認してください。
設定の結果は `%APPDATA%\svnop\setup.log` に記録されます。

インストーラを使わない場合は、`svnop-<バージョン>-win-x64.zip` を任意のフォルダに展開し、`svnop setup tortoisesvn` を実行してください（方針はその場で聞かれます）。

## 初期設定（TortoiseSVN）

インストーラで設定した場合、残りは既存のパスワードの取り込みだけです。

1. **既存のパスワードを 1Password に取り込む**（TortoiseSVN に保存済みのパスワードがある場合）

   ```
   svnop import --dry-run      # 取り込める一覧を見る
   svnop import --adopt        # 番号を選んで取り込む（all で全件）
   ```

   `--adopt` を付けると、取り込んだキャッシュは svnop の管理下に入ります（`svnop lock` で消せるようになります）。

2. **1Password からキャッシュを作る**（「保存する」方針で、1Password に登録済みの場合。別の PC で登録したときなど）

   ```
   svnop sync
   ```

手作業で設定する場合は、次のように実行します。

```
svnop setup tortoisesvn --vault SVN --cache keep --sync-on-start true
```

## 初期設定（msys2 の svn）

msys2 の svn（`pacman -S subversion` で入れたもの）は Windows の暗号化キャッシュを使えません。
そこで gpg-agent の pinentry 経由で 1Password を使います。認証キャッシュの方針とは関係なく動きます。

1. msys2 に subversion を入れます。

   ```bash
   pacman -S subversion
   ```

2. 設定します（Windows のコマンドプロンプトなどから）。インストーラのタスクで選んだ場合は不要です。

   ```
   svnop setup msys2
   ```

   次のことを行います。`svnop setup --remove msys2` で元に戻せます。

   | 対象 | 内容 |
   |---|---|
   | `~/.gnupg/gpg-agent.conf` | pinentry を `svn-op-pinentry.py` にする。元のファイルは `.svnop-bak` に退避する |
   | `~/.config/svn-op/config.json` | pinentry の設定を作る |
   | `~/.bashrc` | msys2 のシェルで gpg-agent を起動する数行を、`# >>> svnop >>>` で囲んで足す |
   | `~/.subversion/auth/svn.simple` | 1Password の各 realm について、ユーザー名だけのキャッシュを作る（初回から 1Password を使うため） |

3. msys2 のシェルを開き直して `svn` を使います。

> msys2 に subversion を入れていない場合、msys2 のシェルの `svn` は PATH 上の TortoiseSVN の `svn.exe` になります。
> その場合は TortoiseSVN の `svn.exe` として動くので、認証キャッシュを「保存する」方針にしてください。

## 日常の使い方

| 場面 | 何が起きるか |
|---|---|
| TortoiseSVN の認証ダイアログが出た | agent が 1Password の値を入れて OK を押します（通知が出ます）。「保存する」方針なら、成功したときにキャッシュにも保存されます |
| 1Password に登録のないリポジトリ | agent が入力ウインドウを出します。入力した値は 1Password に記録してから、ダイアログに入れます |
| サーバ側でパスワードが変わった | 自動入力の直後に同じダイアログが再び出ると、agent は失敗と判断して入力ウインドウを出します。新しい値で 1Password を更新します |
| `svn.exe` でパスワードを入力して保存した | agent がキャッシュの変化に気付き、確認のうえ 1Password に記録します |
| 1Password でパスワードを変えた | 「保存する」+ ログオン時の同期なら、次のログオンで反映されます。すぐ反映するときはトレイの「1Password から同期」か `svnop sync` |
| 新しいリポジトリを事前に登録したい | `svnop add <URL>` |
| ローカルのキャッシュを一時的に消したい | トレイの「キャッシュを消す」か `svnop lock`（戻すときは sync） |

### トレイメニュー

| 項目 | 内容 |
|---|---|
| 1Password から同期 (sync) | キャッシュを 1Password に揃える（「保存する」方針のときだけ） |
| キャッシュを消す (lock) | svnop が書いたキャッシュを消す |
| 設定 | 下の表 |
| ヘルプ (README) | この README を開く |
| ログを開く | `agent.log` を開く |
| 終了 | agent を終了する |

「設定」のサブメニューは次のとおりです。

| 項目 | 内容 |
|---|---|
| ボールト | 使っているボールト（表示のみ） |
| 認証キャッシュにパスワードを: 保存する / 保存しない | 方針の切り替え。「保存しない」にすると既存のキャッシュを消すか、「保存する」にすると今すぐ同期するかを聞きます |
| ログオン時に 1Password から同期する | 「保存する」のときだけ |
| 自動入力したら通知する | |
| 自動入力の後に OK を押す | オフにすると、入力だけして OK は自分で押します |
| 表示言語 | 自動（Windows に合わせる）/ 日本語 / English |
| 設定ファイルを開く | `config.json` を開く |

### 1Password の承認について

1Password は CLI からのアクセスを「プロセスごと」に承認します。承認は 10 分間操作がなければ失効し、最長でも 12 時間で失効します。
svnop では agent がまとめて 1Password にアクセスするので、承認ダイアログは通常「しばらく使っていなかった後の最初の 1 回」だけ出ます。

`svnop list` などのコマンドを直接実行したときは、そのコマンドのプロセスとして 1 回承認が必要です。

## 設定

設定は `%APPDATA%\svnop\config.json` にあります。変更する方法は次の 3 つです。

- トレイメニューの「設定」
- `svnop config <キー> <値>`
- ファイルを直接編集する（agent はメニューを開いたときに読み直します）

| キー | 既定値 | 内容 |
|---|---|---|
| `cacheMode` | （未選択） | `keep`: 認証キャッシュに保存する / `none`: 保存しない |
| `syncOnStart` | `true` | `keep` のとき、ログオン時（agent の起動時）に 1Password からキャッシュを同期する |
| `vault` | （op の既定） | 使う 1Password のボールト |
| `language` | `auto` | 表示言語。`auto`（Windows に合わせる）/ `ja` / `en` |
| `notifyOnFill` | `true` | 自動入力したときに通知する |
| `autoSubmit` | `true` | 自動入力の後に OK を押す |
| `allowPipe` | `true` | msys2 の pinentry からの問い合わせ（名前付きパイプ）に答える。使わないなら `false` |
| `op` | （自動検出） | `op.exe` の場所 |
| `tag` | `svn` | 対象アイテムに付けるタグ |

表示言語は、次の順に決まります。

1. 環境変数 `SVNOP_LANG`（`ja` / `en`。コマンドを一時的に切り替えるとき）
2. `config.json` の `language`
3. Windows の表示言語

同じフォルダにある次のファイルは、svnop が自動で書き込みます。

| ファイル | 内容 |
|---|---|
| `managed.json` | svnop が書いたキャッシュの記録 |
| `realm-map.json` | realm から 1Password のアイテム ID への対応表。秘密情報は入らない |
| `agent.log` / `setup.log` | ログ。パスワードは書かない |

msys2 側の設定は `~/.config/svn-op/config.json` にあります（`svnop setup msys2` が作ります）。

## コマンド一覧

詳しくは `svnop help <コマンド>` を見てください。

| コマンド | 内容 |
|---|---|
| `setup tortoisesvn \| msys2` | 初期設定（`--remove` で解除、`--dry-run` で確認だけ） |
| `config [キー [値]]` | 設定を表示する / 変更する |
| `realm <URL>` | URL の realm（認証の単位）を調べる |
| `add <URL>` | 1Password に登録する（「保存する」方針ならキャッシュにも書く） |
| `find <URL\|realm>` | 1Password に登録があるか確認する |
| `list` | 登録済みの一覧を表示する |
| `import` | ローカルのキャッシュを 1Password に取り込む |
| `sync` / `lock` / `status` | キャッシュを同期する / 消す / 状態を見る |
| `cache [<URL>]` | キャッシュの realm 一覧を表示する |
| `agent [--stop \| --status]` | agent を起動する / 終了する / 状態を確認する |
| `gpg-init` | msys2 の svn 用にユーザー名だけのキャッシュを作る |
| `dialogs` | 画面上の TortoiseSVN ダイアログを調べる（調査用） |
| `--version` | バージョンを表示する |

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
svn.exe     ──(%APPDATA%\Subversion の認証キャッシュ)◀── sync / 監視 ── svnop agent   ※「保存する」方針のとき
msys2 svn   ──▶ gpg-agent ──▶ svn-op-pinentry ──▶ svnop call ──(名前付きパイプ)──▶ svnop agent
```

- agent は TortoiseSVN の exe が出した認証ダイアログだけを対象にします。インストール先のパスで確認するので、ほかのプログラムが似たダイアログを出しても入力しません。
- 認証ダイアログの「認証を保存」は、方針が「保存する」なら付けたまま、「保存しない」なら外して送ります。
- キャッシュは Subversion 標準の Windows 暗号化（DPAPI、ユーザー単位）で書きます。
- 名前付きパイプ（`\\.\pipe\svnop-<ユーザー名>`）には、同じユーザーしか接続できません。

## セキュリティについて

### 信頼モデル

- **同じユーザーのプロセス**: agent は、1Password の承認が有効な間、同じ Windows ユーザーのプロセスからのパスワードの問い合わせ（名前付きパイプ）に答えます。gpg-agent のキャッシュと同じ水準の信頼モデルです。使わない場合は `allowPipe` を `false` にしてください。
- **他のユーザーとネットワーク**: パイプは同じユーザーだけが接続でき、ネットワーク（SMB）経由の接続は拒否します。
- **パイプの乗っ取り**: クライアント（pinentry が使う `svnop call`）は、パスワードを送受信する前に、相手が同じユーザーの、同じフォルダにある svnop の exe であることを確かめます。agent も、同じ名前のパイプを他のプロセスが先に作っていたら使いません。
- **書き込みの確認**: パイプ経由の 1Password への記録（save）は、agent が毎回確認を出します。
- **専用ボールト**: SVN 用のボールトを分けると、op に渡す範囲を絞れます。チームでの共有にも使えます。

### 実装上の対策

- **パスワードの扱い**: ログ・コマンドライン・一時ファイルにパスワードを出しません。1Password への書き込みは、JSON を標準入力で渡します。ログの制御文字はエスケープし、op のエラー出力は 1 行目だけを切り詰めて使います。
- **op.exe の確認**: AgileBits の正しい署名がある op.exe だけを使います。設定で場所を指定する場合も絶対パスに限ります。
- **自動入力の対象**: TortoiseSVN のインストール先にある決まった exe（TortoiseProc など）で、正しく署名されたものが出した認証ダイアログだけに入力します。入力の直前に、ダイアログが変わっていないかをもう一度確かめます。
- **realm の調査**: https の証明書を検証します（自己署名のサーバーは `--insecure` で明示）。svn:// の応答には、大きさ・深さ・時間の上限を設けています。
- **キャッシュ**:
  - 「保存する」方針のキャッシュは、TortoiseSVN が自分で保存する場合と同じ形式・同じ強度（DPAPI）です。平文で保存されていたキャッシュは、同期のときに暗号化し直します。
  - 持ち出す PC でキャッシュを残したくない場合は「保存しない」にするか、作業後に lock してください。
  - lock は、svnop が書いたキャッシュファイル（realm から決まる名前のもの）だけを消します。
- **メモリ**: 復号に使ったネイティブのバッファは消去してから解放します。ただし .NET の文字列は消去できないため、処理中のパスワードは一時的にメモリに残ります。

## 困ったとき

| 症状 | 対処 |
|---|---|
| 承認ダイアログが何度も出る | agent が動いているか確認する（`svnop agent --status`）。コマンドを直接実行したときは毎回 1 回出ます |
| `op.exe が見つかりません` | 1Password CLI を入れる。または `svnop config op <パス>` |
| `account is not signed in` | 1Password アプリで「1Password CLI と連携」を有効にし、アプリを解錠する |
| TortoiseSVN で自動入力されない | `svnop dialogs` でダイアログが認識されているか確認する。`%APPDATA%\svnop\agent.log` を見る |
| `svn.exe` が毎回パスワードを聞く | 方針が「保存しない」になっていないか確認する（`svnop config cacheMode`）。「保存する」なら `svnop sync` |
| msys2 の svn が端末でパスワードを聞く | `~/.subversion/auth/svn.simple` にその realm のファイルがあるか確認する（なければ `svnop gpg-init --auth-dir …`）。gpg-agent が起動しているか（`gpgconf --launch gpg-agent`）確認する |
| msys2 のスクリプトから svn を使うと失敗する | 標準入力が端末でないと svn は非対話モードになり、pinentry を呼びません。`--force-interactive` を付けてください |
| msys2 の svn で証明書の確認が出る | msys2 の svn は TortoiseSVN とは別に証明書の受け入れを記録します。一度、端末で受け入れてください |

## アンインストール

「アプリと機能」から svnop をアンインストールします。次のことが自動で行われます。

- agent を終了する
- 自動起動の登録を削除する
- msys2 の設定を元に戻す（設定していた場合）
- PATH から外す

1Password のアイテムとローカルのキャッシュは残ります。キャッシュも消したい場合は、アンインストールの前に `svnop lock` を実行してください。

## ビルドとリリース

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
