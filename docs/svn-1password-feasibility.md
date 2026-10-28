# SVN クライアント認証情報を 1Password に置けるかの調査

調査日: 2026-10-28

## 1. 結論

- **Windows ネイティブ版の SVN（TortoiseSVN 同梱版を含む）には、認証部分を DLL として外に出す仕組みはない。**
  サーバ側の SASL のような差し替え口は、クライアントの認証情報取得には用意されていない。
- **msys2 の svn には使える差し替え口がある。** GPG-Agent 用のプロバイダが入っており、
  gpg-agent はパスワード入力を外部プログラム（pinentry）に任せている。
  1Password を引く自作の pinentry に差し替えれば、svn 本体には手を入れずに連携できる。
- **TortoiseSVN では「1Password から取り出して SVN のキャッシュファイルに書き込む」同期方式が現実的。**
  svn+ssh を使える環境なら、1Password の SSH Agent を使う方法がいちばん素直。

## 2. SVN クライアントの認証の仕組み（ソースで確認）

### 2.1 認証プロバイダとパスワードの保存先

SVN クライアントは `svn_auth_provider_t` を順番に問い合わせて認証情報を得る。
パスワードの保存先は `config` ファイルの `[auth] password-stores` で選ぶ。
既定値は `gnome-keyring,kwallet,keychain,gpg-agent,windows-cryptoapi`。

`libsvn_subr/auth.c` の `svn_auth_get_platform_specific_provider()`:

| 保存先 | 実装 | ビルド条件 |
|---|---|---|
| gnome_keyring / kwallet (libsecret) | **DSO として動的ロード** `libsvn_auth_%s-N.so` | `SVN_HAVE_GNOME_KEYRING` / `KWALLET` / `LIBSECRET` |
| gpg_agent | 本体に組み込み | `SVN_HAVE_GPG_AGENT`（`#ifndef WIN32`） |
| keychain | 本体に組み込み | macOS のみ |
| windows (wincrypt) | 本体に組み込み | `WIN32 && !__MINGW32__` |

- 動的ロードの仕組みは**あるにはある**。ただし、読み込む DLL 名は `gnome_keyring` と `kwallet` に固定されている。
  設定で任意の DLL を指定することはできず、Windows ビルドではこの経路自体がコンパイルされない。
- そのため、Windows ネイティブ版で使える保存先は **wincrypt（DPAPI）だけ**になる。

### 2.2 クライアント側の SASL（svn:// プロトコル）

TortoiseSVN の bin には `libsasl.dll` と `sasl*.dll` が同梱されており、プラグイン機構そのものはある。
ただし `libsvn_ra_svn/cyrus_auth.c` を見ると、SASL のコールバック `SASL_CB_AUTHNAME` / `SASL_CB_PASS` は
**svn の auth（`SVN_AUTH_CRED_SIMPLE`）から値を受け取っているだけ**だった。
SASL プラグインはチャレンジ／レスポンスの方式を実装する層で、認証情報の取得元ではない。

- 1Password を引く SASL 機構プラグインを作って、既存の機構名（CRAM-MD5 など）を名乗らせる、という無理やりな方法は理屈の上では可能。
- ただし使えるのは svn:// だけで、http(s) には効かない。保守性も悪いので**推奨しない**。

### 2.3 TortoiseSVN

TortoiseSVN の `src/SVN/SVNPrompt.cpp` で登録しているプロバイダは次のとおり。

- platform-specific（= wincrypt）
- TSVN 独自の simple プロバイダ（プロセス内のメモリキャッシュ。`may_save = false`）
- ディスクキャッシュ、プロンプトダイアログ

外部プログラム、DLL、フックで認証情報を取る経路は**ない**。
実行時に読み込む `libsvn_tsvn.dll` を差し替える案は、更新のたびに壊れるので対象外とした。

### 2.4 手元環境

- TortoiseSVN の `svn --version` が表示するキャッシュは `Wincrypt cache in %APPDATA%\Subversion` のみ。
  ra_svn は「with Cyrus SASL」でビルドされている。
- msys2 には subversion が**未インストール**。gpg-agent と pinentry はインストール済み。
- 1Password CLI（`op`）は PATH 上に見つからなかった。

## 3. 1Password 連携の案

### 案 A: msys2 の svn から gpg-agent と自作 pinentry を経由する（★ msys2 向け本命）

msys2 の PKGBUILD は `--without-gpg-agent` を指定していない。configure の既定値は gpg-agent 有効なので、
msys2 の svn（MSYS ランタイム、`WIN32` 未定義）では GPG-Agent プロバイダが使える。

処理の流れ:

```
svn ──(GET_PASSPHRASE cache_id=md5(realm), 説明="…password for <realm>", プロンプト="Password for '<user>'")──▶ gpg-agent
gpg-agent ──(キャッシュにない)──▶ pinentry-program = op-pinentry
op-pinentry: SETDESC / SETPROMPT から realm と user を取り出す → `op read op://…` → "D <password>" を返す
```

- `gpg_agent.c` によると、ディスク（`svn.simple`）に残るのは**ユーザ名と passtype だけ**で、パスワードは書き込まれない。
- `~/.gnupg/gpg-agent.conf` に `pinentry-program /path/to/op-pinentry` を書くだけで差し替えられる。
  pinentry のプロトコルは Assuan の行プロトコルで、`OPTION` / `SETDESC` / `SETPROMPT` / `GETPIN` / `BYE` などに応答すればよい。
  短いスクリプトで書ける。
- realm から 1Password の項目への対応付けは、次のどちらかで行う。
  - 1Password 側の項目に `svn_realm` フィールドを持たせ、`op item list` で検索する。
  - realm → `op://Vault/Item/password` の対応表ファイルを持つ。
- gpg-agent のキャッシュ（`default-cache-ttl`）が効いている間は、1Password に問い合わせない。
- 注意点:
  - `--non-interactive` を付けると svn は `--no-ask` を送るため、pinentry は呼ばれない（キャッシュに当たったときだけ通る）。
  - 初回は `svn.simple` にユーザ名がまだないので、ターミナルのプロンプトで一度入力する必要がある。
  - pinentry に別の処理を兼ねさせる場合は、svn からの要求かどうかを SETDESC の文言で見分け、それ以外は本物の pinentry に回す。
- ~~要検証: ソケットの互換性~~ → 実機で確認し、問題なかった（3.1 を参照）。

### 3.1 案 A の実機検証（2026-10-28）

検証環境:

- msys2 の subversion 1.14.5（`svn --version` が表示するキャッシュは「GPG-Agent」）
- gnupg 2.4.8
- ローカルの svnserve（svn://、realm `TestRealm`）

分かったこと:

- svn → gpg-agent → pinentry の経路はそのまま動く。pinentry には次の内容が届く。
  ```
  SETKEYINFO u/<md5(realm)>
  SETDESC Enter your Subversion password for <svn://127.0.0.1:23691> TestRealm
  SETPROMPT Password for 'test':
  GETPIN
  ```
  `SETKEYINFO` の値は md5(realm) と一致する。そのため説明文の言語が変わっても、realm の取り出しが正しいかを確かめられる。
- 認証に失敗すると、svn は `CLEAR_PASSPHRASE` を送ったあと再び問い合わせてくる。2 回目には `SETERROR` が付く
  （`gpg_agent.c` の `next_creds` の処理どおり）。これを使えば「1Password の値が古い」ことを検知して、値を更新できる。
- **落とし穴 1**: GNUPGHOME が長いパスだと gpg-agent が起動しない（UNIX ソケットのパス長の上限）。短いパスに置く必要がある。
- **落とし穴 2**: stdin が端末でないと、svn は自動的に non-interactive になる。
  - その場合は `--no-ask` が付くので pinentry は呼ばれず、agent のキャッシュに当たったときしか通らない。
  - スクリプトから使うときは `--force-interactive` を付ける。端末から手で打つ分には問題ない。
- 初めての realm では、svn は agent に問い合わせない（`svn.simple` のファイルがないため）。まず通常のプロンプトでユーザ名を聞き、
  `passtype=gpg-agent` とユーザ名だけをファイルに保存する。2 回目からは pinentry 経由になる。
  - TortoiseSVN 向けツール（`svnop add`）でこのファイルを先に作っておけば、初回から pinentry 経由にできる。

試作: [msys2/svn-op-pinentry.py](../msys2/svn-op-pinentry.py)

- svn からの問い合わせなら、realm とユーザ名で 1Password（`op` CLI）を引く。
- 見つからないとき、または SETERROR が付いているときは、fallback の pinentry（既定は pinentry-w32）で入力させる。
  確認ダイアログ（CONFIRM）を出したうえで、1Password に作成または更新する。
- svn 以外の問い合わせ（gpg の鍵パスフレーズなど）は、fallback にそのまま中継する。そのため既存の gpg の使い方を壊さない。
- モックのバックエンド（JSON ファイル）で、次のシナリオを確認した。
  - A: 未登録の realm → 入力 → 記録。誤ったパスワードを入れた場合は、再入力で上書きされる。
  - B: agent のキャッシュを消す → 1Password の値で入力なしに通る。
  - C: サーバ側でパスワードを変更 → 1Password の値で失敗 → 再入力 → 1Password を更新。
  - D: svn 以外の問い合わせ → fallback に中継される。
- 実際の 1Password（op CLI 2.41.1、デスクトップアプリ連携、Personal ボールト）でも A〜C のシナリオが通った（3.2 を参照）。
- 記録のタイミングについて: svn 側から「認証に成功した」という通知は来ない。
  そのため、入力された時点でいったん記録し、失敗して再び問い合わせが来たら上書きする方式にした（gpg-agent 自身のキャッシュと同じ考え方）。

### 3.2 1Password（op CLI）での実機検証（2026-10-28）

| シナリオ | 結果 | 所要時間 | op の呼び出し |
|---|---|---|---|
| A: 未登録 → 入力 → 作成 | OK。アイテム `SVN test @ <realm>` が作成された（タグ svn、`svn_realm` 付き） | 22.6 秒 | get（該当なし）＋ create |
| A2: agent のキャッシュに当たる | OK | 0.09 秒 | なし |
| B: agent のキャッシュを消す → 取得 | OK | 11.4 秒 | get |
| C: サーバ側でパスワード変更 → 失敗を検知 → 再入力 → 更新 | OK。1Password 側の値が新しいパスワードに変わった | 32.0 秒 | get ×2 ＋ edit |

- 作成は JSON テンプレートを stdin で渡す（`op item create -`）。更新は取得した JSON を書き換えて `op item edit <id>` の stdin で渡す。
  **どちらもパスワードをコマンドラインに出さずに済む**。
- **承認ダイアログは op を 1 回呼ぶごとに出る**。上の所要時間のほとんどは承認待ちの時間である。
  - 1Password の CLI と SDK はどちらも、承認を「プロセスごと・アカウントごと」に管理する。操作が 10 分なければ失効し、最長でも 12 時間で失効する。
    Windows では、op を呼び出したプロセスの PID と起動時刻で区別する。
  - PowerShell から続けて呼ぶと、2 回目以降は承認なしで約 3 秒で返った。
    msys2 から呼ぶと、MSYS ランタイムがプロセスを起動し直すため、呼び出し元が毎回別のプロセスになる。そのため毎回承認が出る。
  - gpg-agent は問い合わせのたびに新しい pinentry を起動する。そのため、**pinentry から直接呼ぶ限り、op でも SDK でも「agent のキャッシュが切れるたびに承認 1 回以上」**になる。
- 運用で減らす方法: gpg-agent の `default-cache-ttl` / `max-cache-ttl` を長めにする（例: 8 時間 / 12 時間）。
  そうすれば、realm ごとに 1 日 1 回程度の承認で済む。
- 根本的に減らす方法: **常駐する窓口プロセスに 1Password へのアクセスをまとめる**（TortoiseSVN 向けツール `svnop` の常駐部分を兼ねる）。
  pinentry は名前付きパイプでその窓口に問い合わせる。
  窓口が 1 プロセスなので、承認は「10 分操作がなければ 1 回」で済む。詳しくは [tortoisesvn-tool-plan.md](tortoisesvn-tool-plan.md) を参照。
- SDK について: デスクトップアプリ連携は Go / JS / Python 向けにある（ベータ。.NET 版はない）。
  承認の単位は CLI と同じなので、常駐プロセスで使う場合に限り、op プロセスの起動コスト（約 3 秒）を省ける利点がある。

追記: 常駐窓口（`svnop agent`）は実装済み。pinentry の設定に `"svnop": "/d/.../svnop.exe"` を入れておくと、
agent 経由で 1Password を引く。そのため承認はほぼ出なくなる（詳しくは tortoisesvn-tool-plan.md の段階 3 を参照）。

導入手順（案）:

```
# ~/.gnupg/gpg-agent.conf
pinentry-program /path/to/svn-op-pinentry.py
default-cache-ttl 3600

# ~/.subversion/config
[auth]
password-stores = gpg-agent

# ~/.config/svn-op/config.json（任意）
{"vault": "Dev", "fallback": "/usr/bin/pinentry-w32.exe"}
```

### 案 A': msys2 の CLI を wrapper で包む（最小構成）

svn 1.10 以降には `--password-from-stdin` がある。

```bash
op read "op://Dev/svn-example/password" | svn --username foo --password-from-stdin --no-auth-cache update
```

シェル関数で URL から 1Password の項目を引くようにすれば、すぐに使える。
ただし、svn のサブコマンドごとに対象 URL を判定する処理が必要になる。

### 案 B: TortoiseSVN 向けに wincrypt キャッシュへ同期する（★ TortoiseSVN 向け現実案）

> 認証ダイアログへの差し込み案と、URL から realm を調べる機能を含む詳しい計画は、[tortoisesvn-tool-plan.md](tortoisesvn-tool-plan.md) にある。

TortoiseSVN も msys2 以外の svn も、`%APPDATA%\Subversion\auth\svn.simple\<md5(realm)>` を読む。
このファイルの形式は単純である。

- hash-dump 形式（`K len / key / V len / value … END`）
- キー: `svn:realmstring`、`username`、`passtype` = `wincrypt`、`password`
- `password` の値は `CryptProtectData(data, L"auth_svn.simple.wincrypt", NULL entropy, …, CRYPTPROTECT_UI_FORBIDDEN)` の結果を base64 にしたもの

小さなツール（PowerShell か C#、`op` CLI を使う）で次の処理をする。

- 1Password の「SVN」タグ付き項目を列挙し、realm・ユーザ名・パスワードから上記のファイルを生成する。
- 「アンロック」で書き込み、「ロック」やログオフで削除する。
- 1Password で変更したパスワードを、次の同期でキャッシュに反映する。

得られるもの:

- パスワードの正本を 1Password に一本化できる。
- パスワード変更が全マシンに広がる。
- TortoiseSVN 本体は無改造のまま使える。

残る課題:

- 同期している間は、DPAPI で暗号化されたコピーがディスクに残る（現状と同程度の安全性）。
- 呼ばれたときに取りに行く方式ではないので、realm を事前に知っておく必要がある。
  realm は既存キャッシュの `svn:realmstring` から拾える。

### 案 C: svn+ssh と 1Password SSH Agent（サーバ側で使えるなら最良）

- 1Password の SSH Agent は、Windows では `\\.\pipe\openssh-ssh-agent` で待ち受ける。
- TortoiseSVN の `config` の `[tunnels]` に `ssh = C:/Windows/System32/OpenSSH/ssh.exe` を指定すれば、TortoisePlink の代わりに Windows OpenSSH を使える。
- msys2 からも `SVN_SSH` に Windows の ssh.exe を指定すれば同じ agent を使える。
- パスワードは使わず、鍵を 1Password が管理し、使うたびに承認ダイアログが出る形になる。
- サーバ側で svn+ssh を許可しているかどうかで使えるかが決まる。

### 却下・保留した案

- TortoiseSVN を改造してプロバイダを追加する: 技術的には簡単（`svn_auth_provider_t` を 1 つ足すだけ）。
  ただし独自ビルドを保守し続ける必要がある。上流に「外部 credential helper」機能として提案するなら意味がある。
- SASL プラグインを使う: 2.2 のとおり、svn:// 限定で無理がある。
- DLL ハイジャックで差し替える: 対象外。

## 4. 推奨

1. svn+ssh を使えるサーバなら **案 C**。
2. http(s) と svn:// では、
   - msys2 の CLI は **案 A**（まず案 A' で運用を始めてもよい）。
   - TortoiseSVN は **案 B**。
3. 案 A と案 B は、どちらも realm から 1Password の項目への対応付けを同じ規約で扱える
   （項目に `svn_realm` フィールドを持たせる）。共通化して 1 つのツールにまとめられる。

## 参照ソース

- Subversion `subversion/libsvn_subr/auth.c`, `gpg_agent.c`, `win32_crypto.c`, `libsvn_ra_svn/cyrus_auth.c`, `configure.ac`（apache/subversion trunk）
- MSYS2-packages `subversion/PKGBUILD`（1.14.5）
- TortoiseSVN `trunk/src/SVN/SVNPrompt.cpp`, `TSVNAuth.cpp`（sourceforge）
