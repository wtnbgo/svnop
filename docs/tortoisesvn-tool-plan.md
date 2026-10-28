# TortoiseSVN 向け 1Password 連携ツールの計画

仮の名前は `svnop`。msys2 向けの [svn-op-pinentry](../msys2/svn-op-pinentry.py) と同じ 1Password の項目を使う。

## 0. 前提として確認済みのこと

- TortoiseSVN には外部の認証プロバイダを差し込む口がない（[調査レポート](svn-1password-feasibility.md) を参照）。
- 認証キャッシュは `%APPDATA%\Subversion\auth\svn.simple\<md5(realm)>` にある。
  - hash-dump 形式で、`passtype=wincrypt` のときは password に DPAPI＋base64 の値が入る。
  - 手元にはこの形式の項目が 123 件あり、すべて wincrypt だった。
- realm の文字列は `<scheme://host:port> AuthName` の形。
  - 既定ポートでもポート番号が明示される（例 `<https://HOST:443> VisualSVN Server`）。
  - svn:// で realm を設定していないサーバでは、AuthName の位置にリポジトリの UUID が入る。
  - libsvn 共通の書式なので、**msys2 の svn と TortoiseSVN で同じ realm になる**。そのため 1Password の項目を共用できる。
- TortoiseSVN の認証ダイアログ（`CSimplePrompt`）の構成:
  - `IDC_REALM`: realm を表示する static コントロール
  - `IDC_USEREDIT`: ユーザ名の edit
  - `IDC_PASSEDIT`: パスワードの edit
  - `IDC_SAVECHECK`: 「認証情報を保存」チェックボックス

## 1. 1Password 側の項目の形（msys2 と共通）

| 項目 | 値 |
|---|---|
| カテゴリ | Login |
| タグ | `svn` |
| `username` / `password` | 通常のフィールド |
| `svn_realm` | realm 文字列（検索キー） |
| website | 任意（リポジトリの URL。人が見て分かるようにするため） |

ツールとローカルの対応表（realm → item id。秘密情報は含まない）は `%APPDATA%\svnop\realm-map.json` に置く。

## 2. 機能

### 2.1 `svnop realm <URL>`: URL から realm を調べる

- **http / https**: 認証なしで `OPTIONS` を送る → 401 応答の `WWW-Authenticate: Basic realm="..."`（Digest も同様）から
  `<scheme://host:port> realm` を組み立てる。
  - 返ってきたのが `Negotiate` / `NTLM` だけの場合（AD の SSPI 認証）は、パスワードを保存する必要がない。そう表示する。
  - プロキシは `%APPDATA%\Subversion\servers` の `http-proxy-*` 設定に従う。
- **svn://**: ra_svn のハンドシェイクを最小限だけ実装する。
  greeting → クライアントの応答 → サーバの auth-request `( success ( ( mechs ) realm ) )` から realm を取り出す。
- **svn+ssh://**: 対象外（ssh 側の認証になるため）。
- 補助機能: 既存の svn.simple キャッシュからホスト名が一致する realm を一覧表示する。

### 2.2 `svnop add <URL> [--user U]`: 登録

1. 2.1 で realm を調べる。
2. ユーザ名とパスワードを入力させる（自前のダイアログ）。既存の 1Password 項目を選んで紐づけることもできるようにする。
3. 1Password に作成または更新する（`svn_realm` を付ける）。
4. 任意: その場で 2.4 の同期を 1 件だけ行う。

### 2.3 `svnop import`: 既存のキャッシュを 1Password に移す

- svn.simple の wincrypt 項目を列挙し、`CryptUnprotectData` で復号する（同じユーザでのみ可能。description が `auth_svn.simple.wincrypt` であることを確認する）。
- 1Password に作成する。realm とユーザ名が同じ項目がすでにあれば飛ばすか比較する。
- 一覧を見せて選ばせる。手元の 123 件には使っていない項目も混ざっているはずなので、全件を自動では移さない。
- 任意: 移した後でローカルのキャッシュを消す。

### 2.4 `svnop sync` / `svnop lock`: wincrypt キャッシュへの同期（ご要望の本線）

- **sync**: タグ `svn` の項目について、`svn.simple\<md5(realm)>` を生成する。
  - キーは `svn:realmstring` / `username` / `passtype=wincrypt` / `password`。
  - password は `CryptProtectData(pw, L"auth_svn.simple.wincrypt", NULL, ..., CRYPTPROTECT_UI_FORBIDDEN)` の結果を base64 にした値。
  - svnop が書いたファイルは管理リストに記録する。
- **lock**: 管理リストにあるファイルだけを消す。手で保存したキャッシュには触らない。
- 運用の選択肢:
  - ログオン時に sync し、ログオフ時に lock する。
  - 一定時間が経ったら自動で lock する。
  - 「作業中だけ unlock」するトレイアイコンを置く。
- 利点: TortoiseSVN だけでなく、TortoiseSVN 同梱の svn.exe や、wincrypt を読む他のツール（IDE 連携など）にも効く。
- 欠点: 同期している間は、DPAPI で暗号化されたコピーがディスクに残る。

### 2.5 `svnop watch`: 認証ダイアログに差し込む（常駐。あとから出てきた案）

1. `SetWinEventHook(EVENT_OBJECT_SHOW)` を out-of-context で登録する。
   TortoiseSVN の bin ディレクトリにある exe のプロセスだけを対象にする（実行ファイルのパスで照合する）。
2. `#32770` ダイアログのうち、次の 2 つがそろったものを TortoiseSVN の認証ダイアログとみなす。
   - `ES_PASSWORD` スタイルの edit がある。
   - `^<\w+://[^>]+> ` に一致する static テキストがある。
   リソース ID に依存しない判定にしておく。
3. 認証ダイアログなら、その static テキストを realm として 1Password を引く。
   - **見つかった場合**: `WM_SETTEXT` でユーザ名とパスワードを入れ、保存チェックを外し（設定で変えられるようにする）、OK を押す。
   - **見つからない場合**: 自前の小さなポップアップを出す（「1Password に登録して入力」）。
     そこで入力された値を 1Password に記録してから、ダイアログに流し込む。
     他プロセスのパスワード欄は `WM_GETTEXT` で読めないので、ユーザには TortoiseSVN のダイアログではなく自前のポップアップに入力してもらう。
4. 失敗の検出: 自動入力した後、同じプロセスで同じ realm のダイアログが短時間のうちに再び出たら、認証に失敗したとみなす。
   その場合は新しいパスワードを聞いて 1Password を更新する（msys2 の pinentry で SETERROR を受けたときと同じ扱い）。

- 利点:
  - ディスクにコピーが残らない。
  - realm を前もって調べる必要がない（ダイアログが realm を教えてくれる）。
- 欠点:
  - 常駐が必要。
  - TortoiseSVN のダイアログの作りが変わると影響を受ける（ただし長年変わっていない）。
  - コンソールの svn.exe が出すプロンプトには効かない。
- セキュリティ上の注意: 偽のダイアログにパスワードを流し込まないよう、**プロセスの実行ファイルのパスと署名を照合する**。

### 2.6 1Password への問い合わせ窓口（msys2 の pinentry と共用）

実機検証で次のことが分かった。

- 1Password の承認は op / SDK を呼ぶプロセス単位である。
- msys2 の pinentry は問い合わせごとに新しいプロセスになるので、毎回承認が出る。

そこで、常駐する svnop（2.5 の watch と同じプロセス）に 1Password へのアクセスをまとめる。

- 名前付きパイプ `\\.\pipe\svnop-<ユーザ SID>` を用意し、ACL は現在のユーザだけに絞る。
  - 要求は `find realm user` と `save realm user password` の 2 種類。
- svn-op-pinentry は、窓口が動いていればそこに問い合わせ、動いていなければ op を直接呼ぶ（今の動作）。
  - msys2 からは `\\.\pipe\...` をファイルとして開けばよい。
- 窓口の中では op を毎回同じ親プロセスから呼ぶか、SDK を使う。そうすれば承認は「10 分操作がなければ 1 回」で済む。
- 注意: 窓口は、承認済みの間は同じユーザのどのプロセスにも値を返す。
  - gpg-agent のキャッシュや wincrypt のキャッシュと同じ水準の信頼モデルである。
  - 必要なら、接続してきたプロセスの実行ファイルのパスで呼び出し元を制限する（`GetNamedPipeClientProcessId`）。

## 3. 実装方針

- 言語: **C#（.NET 8、単一 exe）**。
  - DPAPI（`ProtectedData`）、Win32 のフック、トレイアイコン、ダイアログを 1 つの exe にまとめられる。
  - CLI とトレイ常駐を同じ exe で兼ねる。
- 1Password との接続: `op` CLI（デスクトップアプリ連携。生体認証で承認する）。
  - 作成は `op item create -` の stdin に JSON を渡す。更新は `op item edit <id>` の stdin に JSON を渡す。
    どちらも実機で確認済みで、パスワードはコマンドラインに出ない。
  - op は常駐プロセスから呼ぶ（承認は 10 分操作がなければ 1 回。2.6 を参照）。
    1 回の呼び出しに約 3 秒かかる（op プロセスの起動を含む）ので、結果をメモリに短時間だけ持つことも検討する。
  - SDK（Go / JS / Python）は .NET 版がないので、C# から使う場合は op CLI で十分。
- 1Password を経由しない部分（realm の調査、wincrypt ファイルの読み書き）は、op がなくても単体でテストできるようにしておく。

## 4. 段階

| 段階 | 内容 | 確認方法 |
|---|---|---|
| 1 | 1Password とのやりとり層（find / save）と `realm` / `add` | ローカルの svnserve と、Apache または VisualSVN の 401 応答で realm が一致すること |
| 2 | `import` / `sync` / `lock` | sync したキャッシュで TortoiseSVN がダイアログなしで通ること。lock で消えること |
| 3 | `watch`（ダイアログへの差し込み） | 認証ダイアログが出たとき自動入力されること。誤ったパスワードの後に更新の流れに入ること |

段階 1 と段階 2 で、ご要望の「URL ごとに realm を調べて登録し、同期する」が完成する。段階 3 は追加。

### 段階 1 の実装状況（2026-10-28）

ソースは [svnop/](../svnop/)（C#、.NET 10）。ビルドは `dotnet build -c Release`。

| コマンド | 内容 |
|---|---|
| `svnop realm <URL>...` | realm を調べる |
| `svnop add <URL> [--user U] [--realm R]` | 1Password に登録または更新する（パスワードは入力時に伏せ字） |
| `svnop find <URL\|realm> [--user U]` | 登録されているかを確認する |
| `svnop list` | 登録済みのアイテムを一覧表示する |
| `svnop cache [<URL>]` | ローカルの svn.simple の realm を一覧表示する |

確認できたこと:

- **realm の一致**: ローカルの svnserve と Basic 認証の HTTP サーバで、svnop が調べた realm と TortoiseSVN の svn.exe がキャッシュに保存した realm が**完全に一致**した。
  - **realm に `"` を含む場合の扱い**: svn（serf）は `realm="a \"b\""` の `\` を外さず、`a \"b\"` をそのまま realm にする。svnop もこれに合わせた。
  - **Negotiate と Basic を両方返すサーバ**: svn は先に Windows 認証（Negotiate）を試す。svnop は Basic の realm を返す。
    実際にパスワードが要るかどうかは、Windows 認証が通るかどうかで決まる。
- **本物の 1Password での動作**: 新規登録、検索、パスワード更新、一覧表示のすべてが通った。
  - 承認は svnop を 1 回実行するごとに 1 回で済んだ。同じプロセスの 2 回目以降の op 呼び出しでは承認が出ない。
  - 所要時間は、新規登録が約 16 秒、検索が約 11 秒。どちらも承認待ちの時間を含む。
- **msys2 との共用**: svnop で登録したアイテムを、msys2 の svn-op-pinentry 側から引けた。

未実装（段階 2 以降）:

- msys2 用の svn.simple を事前に作る処理（`passtype=gpg-agent`）
- プロキシの設定（`servers` ファイル）の反映
- 常駐する窓口（2.6）

実サーバで確認したこと（2026-10-28）:

- svn:// の実サーバに対して `svnop realm` で調べた realm（`<svn://HOST:3690> <リポジトリ UUID>`）が、TortoiseSVN のキャッシュにある realm と完全に一致した。
- **.NET の Uri は svn:// の既定ポートを知らない**（-1 になる）ので、3690 で補うように修正した。
- **svnserve では realm がリポジトリごとに異なる**:
  - 1 つのホストに対して、キャッシュには 94 件の realm があった。
  - realm には、設定された名前か UUID が入っている。
  - どれも同じユーザ名だった。
  - 「1 realm ＝ 1 アイテム」にすると、1Password のアイテムが realm の数だけ増える。
  - → 段階 2 の import の前に、**同じ資格情報を使う realm を 1 つのアイテムにまとめる形**を決める必要がある（6 章を参照）。

- https の実サーバ（VisualSVN Server）でも、`<https://HOST:443> VisualSVN Server` がキャッシュの realm と完全に一致した。
  - VisualSVN Server の realm はサーバ単位で、リポジトリによらず 1 つだった。

### 段階 2 の実装状況（2026-10-28）

| コマンド | 内容 |
|---|---|
| `svnop import [--all \| --realm R] [--update] [--adopt] [--dry-run]` | ローカルのキャッシュ（wincrypt / 平文）を復号して一覧にし、番号で選んで 1Password に登録する |
| `svnop sync [--force] [--dry-run]` | タグ `svn` のアイテムを wincrypt 形式で書き出す |
| `svnop lock [--force]` | svnop が書いたファイルだけを消す |
| `svnop status` | 管理中のファイルの状態（同期済み / 変更あり / なし）を表示する |
| 共通 `--auth-dir DIR` | svn.simple の場所を変える（検証用） |

import の動作:

- 1Password に登録済みのものは飛ばす。`--update` を付けると、値が違うものだけ更新する。
- `--adopt` を付けると、取り込んだキャッシュを svnop の管理下に置く。以後は lock で消える。

sync の動作:

- 1 回の実行で `op item list` と `op item get -`（一覧の JSON を stdin で渡して一括取得）を 1 回ずつ呼ぶだけで済む。承認も 1 回。
- 書いたファイルは `%APPDATA%\svnop\managed.json` に SHA-256 と一緒に記録する。
- 既存のファイルが 1Password と同じ内容なら、書き換えずに管理下に置く。内容が違えば飛ばす（`--force` で上書き）。
- svnop が書いた後で TortoiseSVN などが書き換えたファイルは、sync でも lock でも触らない（`--force` で上書き・削除）。

wincrypt 形式:

- `CryptProtectData` を直接呼ぶ（説明文字列 `auth_svn.simple.wincrypt`、エントロピーなし、改行なし base64）。
- .NET の `ProtectedData` は説明文字列を指定できず、svn 側の照合で弾かれるため使わない。

検証結果:

| 内容 | 結果 |
|---|---|
| TortoiseSVN の svn.exe が作ったキャッシュを import → 1Password | 登録できた |
| 1Password → sync で空のディレクトリへ書き出す → **svn.exe がそのキャッシュだけで認証を通る** | OK |
| lock で削除 | 削除後は svn.exe が認証できない（期待どおり） |
| 同じ内容のキャッシュがある場所に sync | 書き換えずに管理下へ追加。lock で削除された |
| 本番のキャッシュに `import --dry-run`（読み取りのみ） | 123 件すべて復号できた |

本番への導入（2026-10-28）:

- 1Password にボールト「SVN」を作り、`config.json` を `{"vault":"SVN"}` にした。
- `svnop import --all --adopt` で、本番キャッシュ 123 件を登録した（承認 1 回、所要時間は数分）。
  ローカルのキャッシュは、svnop の管理下（lock の対象）に入っている。
- `svnop sync --dry-run` の結果は「最新 123 件」。1 回の承認で 123 件をまとめて取得し、約 70 秒かかった。
- 修正したバグ:
  - op の stdin に大きな JSON を書き込んでいる間に op の出力でパイプが詰まり、デッドロックしていた。
    stdout の読み取りを先に始めるようにした。
  - 内容が同じ管理対象ファイルも書き直していた。「最新」として飛ばすようにした。

1Password 側の整理（ボールトとタグ）:

- 専用ボールト（例:「SVN」）を作り、`config.json` の `vault` に指定するのがおすすめ。
  - ほかのアイテムと混ざらない。
  - チームで共有したり、権限を分けたりできる。
- タグは `svn` が既定（`config.json` の `tag` で変えられる）。`svn/取引先名` のような階層タグも付けられる。

### 段階 3 の実装状況（2026-10-28）

| コマンド | 内容 |
|---|---|
| `svnop agent` | 常駐を開始する（コンソールなしの子プロセスとして起動し、トレイにアイコンを出す。多重起動は Mutex で防ぐ） |
| `svnop agent --stop` / `--status` | 常駐を終了する / 動いているか確認する |
| `svnop call` | stdin の 1 行 JSON を agent に中継する（msys2 の pinentry 用） |
| `svnop dialogs [--fill USER]` | 表示中の TortoiseSVN のダイアログを調べる（調査用） |

agent の動作:

- 300 ミリ秒ごとにウインドウを列挙し、TortoiseSVN の認証ダイアログを探す。判定条件は次のすべて。
  - 実行ファイルが TortoiseSVN の bin にある（インストール先はレジストリの `Directory` から取る）。
  - クラスが `#32770`。
  - ES_PASSWORD の edit がある。
  - `<scheme://host:port> ` で始まる static がある。
- 見つけたら 1Password を引き、ユーザ名とパスワードを `WM_SETTEXT` で入れ、「認証を保存」を外して OK を押す。
  - `config.json` の `autoSubmit` / `saveAuth` で、OK を押すかどうかと保存を外すかどうかを変えられる。
- 1Password に登録がないとき、または自動入力の後 60 秒以内に同じプロセス・同じ realm のダイアログが再び出たとき（認証失敗とみなす）は、
  自前の入力ウインドウを出す。入力された値を 1Password に記録または更新してから、ダイアログに入れる。
- 名前付きパイプ `\\.\pipe\svnop-<ユーザ名>` を開く。ACL は現在のユーザだけ。1 行 JSON で `find` / `save` / `ping` / `quit` を受け付ける。
- op を呼ぶのは常に agent のプロセスなので、承認は 10 分操作がなければ 1 回で済む。
- ログは `%APPDATA%\svnop\agent.log` に出す（パスワードは書かない）。

実際のダイアログの構成（TortoiseSVN 1.14.8）:

| コントロール | ID | 中身 |
|---|---|---|
| Static | 1220 | realm |
| Edit | 1027 | ユーザ名 |
| Edit | 1004 | パスワード（ES_PASSWORD） |
| Button | 1023 | 「認証を保存」チェック |
| Button | 1 | OK |

判定にはこれらの ID を使っていない。

- 誤ったパスワードで OK を押すと、同じプロセスが同じ realm で新しいダイアログを出す。失敗の検知はこれを利用している。
- 「認証を保存」を外して入力すれば、本番のキャッシュに何も書き込まれないことも確認した。

検証結果（ローカルの svnserve、SVN ボールト）:

| 内容 | 結果 |
|---|---|
| 登録済みの realm でリポジトリブラウザを開く | agent が自動入力して通った（初回は承認待ちを含めて 17 秒） |
| サーバ側でパスワードを変更 → 古い値で失敗 | 再びダイアログが出たのを検知して入力ウインドウを出した。新しい値で 1Password を更新し、通った |
| msys2 の svn → gpg-agent → pinentry → `svnop call` → agent | 失敗を検知して入力し直し、1Password を更新できた。キャッシュを消してから取り直すと 3.7 秒で、承認は出なかった（pinentry から op を直接呼ぶと 11 秒） |

注意点:

- msys2（cygwin）の Python からは、Windows の名前付きパイプを `//./pipe/...` で開けなかった。そのため、svnop.exe の `call` を子プロセスとして起動して中継している。
- pinentry の設定 `svnop`（svnop.exe のパス）を指定しておくと、agent が動いていれば agent を経由し、動いていなければ op を直接呼ぶ。
- 未実装: トレイからの sync / lock。

### 本番への導入（2026-10-28）

- 配置先: `%LOCALAPPDATA%\Programs\svnop`（svnop.exe、svnopw.exe、svn-op-pinentry.py）。
  - svnopw.exe はコンソールを持たない常駐専用の exe（`agentw/svnopw.csproj`。ソースは svnop と共有）。
- 自動起動: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` に「svnop agent」= svnopw.exe を登録した。
- msys2 の設定（当時はシェルスクリプト。現在は `svnop setup msys2` に置き換え）:
  - `~/.gnupg/gpg-agent.conf` を作成（pinentry を差し替え、キャッシュ時間 1 時間 / 最長 8 時間）。
  - `~/.config/svn-op/config.json` を作成。
  - `~/.bashrc` に、msys2 のシェルでだけ gpg-agent を起動する 1 行を追加。
- `svnop gpg-init --auth-dir %USERPROFILE%\.subversion\auth\svn.simple` で、msys2 の svn 用の GPG-Agent 形式キャッシュを 120 件作成した。
  - 既存の 3 件（別の Windows 版 svn ツールが使っていた wincrypt 形式）は、ユーザの依頼で
    `%USERPROFILE%\.subversion\auth\svn.simple.bak-<日付>` に移し、gpg-agent 形式に作り直した。
    元に戻すときは、このフォルダから svn.simple にファイルを戻す。
  - svn:// の実サーバで、msys2 の svn が agent 経由で通ることを確認した（`svn info` が 3.9 秒）。
- その後、msys2 の subversion パッケージを削除した（ユーザの依頼）。
  - msys2 のシェルの `svn` は、PATH 上の TortoiseSVN の svn.exe になった。
  - TortoiseSVN の svn.exe は `%APPDATA%\Subversion` の wincrypt キャッシュを使うので、svnop の sync で賄える。
  - gpg-agent まわりの msys2 設定は残してある（msys2 の svn を入れ直せばそのまま使える）。

### 製品化（2026-10-28）

- **日英両対応**:
  - 表示言語は Windows の表示言語に従う。`SVNOP_LANG=ja|en` で切り替えられる。
  - InvariantGlobalization を使っているため、言語は `GetUserDefaultUILanguage` で判定する。
  - ログは英語で出す。
  - pinentry は `SVNOP_LANG` / `LANG` で切り替える。
- **ヘルプ**: `svnop help` / `svnop help <コマンド>` / `<コマンド> --help`（日英）。
- **自動設定**: `svnop setup tortoisesvn | msys2`。`--remove` で解除、`--dry-run` で確認だけ。
  - 変更内容は `%APPDATA%\svnop\setup.log` にも残す。
  - msys2 側のファイルは `# >>> svnop >>>` / `# <<< svnop <<<` で囲んだ範囲だけを書き換える。
  - `.bashrc` に以前手作業で足した 2 行も、自動で取り除く。
  - 2 回実行しても重複しない。
  - 以上は、一時ホーム（`--home`、検証用）で確認した。
- **ドキュメント**: [README.md](../README.md)（日本語）と [README.en.md](../README.en.md)（英語）。
- **インストーラ**: Inno Setup（`installer/svnop.iss`、`installer/build.ps1`）。
  - ユーザ単位で `%LOCALAPPDATA%\Programs\svnop` に入れる。画面は日英。
  - 選べるタスク: TortoiseSVN 向けの設定（ボールト名を入力するページ付き）、msys2 向けの設定（msys2 があるときだけ表示）、PATH への追加。
  - .NET 10 デスクトップ ランタイムがなければ、ダウンロードページを案内して中止する。
  - op が見つからなければ、導入方法を案内する。
  - 上書きインストールの前に agent を止める。
  - アンインストール時は設定を解除し、PATH から外す。
- **バージョン**: `svnop/Directory.Build.props`（0.1.0）。インストーラにも同じ値を渡す。
- **未確認**: インストーラの実際のインストール・アンインストール（本番環境の設定を書き換えるため、ユーザが実行する）。

### TortoiseSVN の svn.exe（コンソール）への対応（2026-10-28）

svn.exe はコンソールで入力を求めるので、agent の自動入力は効かない。そこで、キャッシュを常に 1Password と揃えておく方針にした。

| 追加したこと | 内容 |
|---|---|
| ログオン時の自動 sync | agent の起動時に 1Password → wincrypt キャッシュを同期する（`config.json` の `syncOnStart`、既定は有効）。1Password がまだ起動していなければ、1 分おきに 5 回までやり直す。123 件で約 70 秒、承認 1 回 |
| トレイメニュー | 「1Password から同期 (sync)」と「キャッシュを消す (lock)」を追加した |
| `svnop add` | 1Password に登録した後、キャッシュにも書く（`--no-cache` で書かない） |
| 画面で記録したとき | agent の入力ウインドウで 1Password に記録した値を、キャッシュにも書く |
| キャッシュの監視 | agent が svn.simple を監視する。svn.exe などが保存した認証情報が 1Password にない、または値が違うときは、確認を出して 1Password に記録し、管理下に入れる。断った内容は、同じ内容のままならもう聞かない |
| 修正 | `op item get <id>` はアーカイブ済みのアイテムも返すので、アーカイブ済みを除外した（svnop / pinentry の両方） |

確認したこと:

- `add` で書いたキャッシュだけで、svn.exe が認証を通った。
- 起動時の自動 sync が動いた。
- svn.exe が保存した新しいパスワードを監視が検知し、確認の後で 1Password が更新された。

確認できていないこと:

- 入力ウインドウで記録したときのキャッシュへの書き込み（`add` と同じ処理を使っている）。

- 実サーバ（https、VisualSVN Server）で確認した。msys2 の svn → gpg-agent → pinentry → agent → 1Password の経路で `svn info` が通った。
  - 証明書（発行元が信頼されておらず、ホスト名も一致しない）の受け入れは、ユーザ自身が端末で行った。

## 5. 未確認のこと

- Apache＋AD（Negotiate と Basic を両方返すサーバ）での実地の動作。

## 6. 検討事項: 複数の realm を 1 つのアイテムにまとめるか

**決定（2026-10-28）: A 案（1 realm ＝ 1 アイテム）にする。**
サーバ側で近いうちに realm を取引先ごとに統一する予定なので、アイテム数は抑えられる見込み。
以下は検討した内容の記録として残す。

| 案 | アイテムの形 | 検索 | 利点 | 欠点 |
|---|---|---|---|---|
| A: 1 realm ＝ 1 アイテム（現状） | タイトル `SVN user @ realm` | タイトルで get（op を 1 回呼ぶ） | 単純で、pinentry から直接引いても速い | アイテムが realm の数だけ増える。パスワードを変えるときに全アイテムを直す必要がある |
| B: 1 アカウント ＝ 1 アイテム | `svn_realm` フィールドを複数持つ（または改行区切り） | realm → item id の索引が必要（list と get の総なめ、または対応表） | パスワード変更が 1 か所で済む | 索引のない状態で引くと op を何度も呼ぶ。常駐する窓口（2.6）があれば、メモリ上の索引で解決できる |

B 案にする場合の import の流れ: ユーザ名とパスワードが同じ realm をまとめる → 1 アイテムにする。
ホスト単位のまとめ（`svn://HOST` 全体で 1 アイテム）も選べるようにする。

- ~~op CLI の細かな動作~~ → 確認済み（stdin での create / edit、`--reveal`、タイトルでの get）。
- `watch` の動作: TortoiseSVN の最新版でダイアログを判定できるか。
  `WM_SETTEXT` の後で MFC の DDX が値を拾うか（OK を押したときに `UpdateData` が走るので拾うはず）。
