using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SvnOp;

/// <summary>
/// svnop setup tortoisesvn | msys2 [--remove]。
/// 何をしたかは画面と %APPDATA%\svnop\setup.log に出す (インストーラから非表示で実行されたときの確認用)。
/// </summary>
static class Setup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "svnop agent";
    const string Begin = "# >>> svnop >>>";
    const string End = "# <<< svnop <<<";

    static string AppDir => Path.GetDirectoryName(Environment.ProcessPath!)!;
    static bool _dry;

    static void Out(string line)
    {
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.AppendAllText(Path.Combine(Config.Dir, "setup.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void Warn(string line) => Out(L.T("注意: ", "Warning: ") + line);

    /// <summary>変更を 1 つ行う。--dry-run なら内容を表示するだけ</summary>
    static void Do(string what, Action act)
    {
        Out((_dry ? "[dry-run] " : "- ") + what);
        if (!_dry)
            act();
    }

    public static int Run(Options o)
    {
        _dry = o.Has("dry-run");
        var target = o.Positional.FirstOrDefault();
        var remove = o.Has("remove");
        return (target, remove) switch
        {
            ("tortoisesvn", false) => TortoiseSvn(o),
            ("tortoisesvn", true) => RemoveTortoiseSvn(),
            ("msys2", false) => Msys2(o),
            ("msys2", true) => RemoveMsys2(o),
            _ => throw new SvnOpException(L.T(
                "対象を指定してください: svnop setup [--remove] tortoisesvn|msys2",
                "specify a target: svnop setup [--remove] tortoisesvn|msys2")),
        };
    }

    // ---- TortoiseSVN / svn.exe ----

    static int TortoiseSvn(Options o)
    {
        Out(L.T("TortoiseSVN 向けの設定", "Setting up for TortoiseSVN"));
        var cfg = Config.Load();

        // 指定された設定を config.json に記録する (省略されたものは今の値のまま)
        var changes = new List<(string Key, string Value)>();
        if (o.Get("vault") is { } vault) changes.Add(("vault", vault));
        if (o.Get("language") is { } lang) changes.Add(("language", lang));
        if (o.Get("cache") is { } cache) changes.Add(("cacheMode", cache));
        else if (!cfg.CacheModeChosen && !Console.IsInputRedirected && !_dry)
            changes.Add(("cacheMode", AskCacheMode()));
        if (o.Get("sync-on-start") is { } sync) changes.Add(("syncOnStart", sync));
        foreach (var (key, value) in changes)
        {
            var before = cfg.Get(key);
            cfg.Set(key, value);  // 値の検査を兼ねる
            if (before != cfg.Get(key))
                Do(L.T($"config.json: {key} = {cfg.Get(key)}", $"config.json: {key} = {cfg.Get(key)}"), cfg.Save);
        }
        Out(cfg.CacheModeChosen
            ? L.T($"- 認証キャッシュ: {(cfg.KeepCache ? "保存する" : "保存しない")}{(cfg.KeepCache ? (cfg.SyncOnStart ? " / ログオン時に同期する" : " / ログオン時に同期しない") : "")}",
                  $"- credential cache: {(cfg.KeepCache ? "save" : "do not save")}{(cfg.KeepCache ? (cfg.SyncOnStart ? " / sync at logon" : " / no sync at logon") : "")}")
            : L.T("- 認証キャッシュ: 未選択 (agent の初回起動時に聞きます)", "- credential cache: not chosen yet (the agent asks at first start)"));
        CheckOp(cfg);

        var w = Path.Combine(AppDir, "svnopw.exe");
        if (!File.Exists(w))
            Warn(L.T($"{w} がありません。自動起動は登録できません", $"{w} not found; autostart cannot be registered"));
        else if (!o.Has("no-autostart"))
            Do(L.T("ログオン時に agent を起動するよう登録 (HKCU\\...\\Run)", "register the agent to start at logon (HKCU\\...\\Run)"), () =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.SetValue(RunName, $"\"{w}\"");
            });

        if (Agent.Call(new() { ["cmd"] = "ping" }, 500) == null)
            Do(L.T("agent を起動", "start the agent"), () => Agent.StartDetached());
        else
            Out(L.T("- agent は起動済み", "- the agent is already running"));

        Out("");
        Out(L.T("次の手順:", "Next steps:"));
        Out(L.T("  既存のキャッシュを 1Password に取り込む:  svnop import --adopt",
                "  import the existing cache into 1Password:  svnop import --adopt"));
        if (cfg.KeepCache)
            Out(L.T("  1Password からキャッシュを作る:          svnop sync",
                    "  write the cache from 1Password:            svnop sync"));
        return 0;
    }

    /// <summary>対話的に認証キャッシュの方針を聞く (コンソールから setup を実行したとき)</summary>
    static string AskCacheMode()
    {
        Console.WriteLine(L.T(
            """

            TortoiseSVN の認証キャッシュ (%APPDATA%\Subversion) にパスワードを保存しますか?
              1) 保存する (おすすめ)  TortoiseSVN もコマンドライン版 svn.exe もキャッシュで認証する。
                                      キャッシュに無いときは svnop が自動入力して保存する
              2) 保存しない           ディスクにパスワードを残さない。TortoiseSVN は毎回 svnop が自動入力する。
                                      コマンドライン版 svn.exe は毎回パスワードを聞かれる
            """,
            """

            Save passwords in TortoiseSVN's credential cache (%APPDATA%\Subversion)?
              1) Save (recommended)   Both TortoiseSVN and the command-line svn.exe authenticate from the cache.
                                      Missing entries are auto-filled by svnop and saved
              2) Do not save          No passwords on disk. svnop fills TortoiseSVN's dialog every time.
                                      The command-line svn.exe asks for the password every time
            """));
        Console.Write(L.T("番号 [1]: ", "Number [1]: "));
        return Console.ReadLine()?.Trim() == "2" ? Config.NoCache : Config.Keep;
    }

    /// <summary>op が使えるか、ボールトがあるかを確かめる (失敗しても設定は続ける)</summary>
    static void CheckOp(Config cfg)
    {
        if (cfg.FindOp() is not { } op)
        {
            Warn(L.T("1Password CLI (op) が見つかりません。winget install AgileBits.1Password.CLI で導入し、"
                     + "1Password アプリの 設定 > 開発者 で「1Password CLI と連携」を有効にしてください",
                     "1Password CLI (op) not found. Install it with winget install AgileBits.1Password.CLI and enable "
                     + "\"Integrate with 1Password CLI\" in the 1Password app (Settings > Developer)"));
            return;
        }
        Out("- op: " + op);
        if (!Trust.IsSignedBy(op, "AgileBits"))
        {
            Warn(L.T("op.exe の署名を確認できません。1Password 公式の op.exe ではない可能性があるので、svnop は使いません",
                     "cannot verify the signature of op.exe. It may not be the official 1Password CLI, so svnop will not use it"));
            return;
        }
        if (_dry)
            return;
        try
        {
            var vaults = new OpClient(cfg).VaultNames();
            if (cfg.Vault.Length > 0 && !vaults.Contains(cfg.Vault))
                Warn(L.T($"ボールト「{cfg.Vault}」がありません。1Password で作るか、op vault create \"{cfg.Vault}\" を実行してください",
                         $"vault \"{cfg.Vault}\" does not exist. Create it in 1Password or run op vault create \"{cfg.Vault}\""));
            else
                Out(L.T($"- ボールト: {(cfg.Vault.Length > 0 ? cfg.Vault : "(op の既定)")}",
                        $"- vault: {(cfg.Vault.Length > 0 ? cfg.Vault : "(op default)")}"));
        }
        catch (SvnOpException e)
        {
            Warn(L.T("1Password に接続できません。1Password アプリで CLI 連携が有効か確認してください: ",
                     "cannot reach 1Password. Check that CLI integration is enabled in the 1Password app: ") + e.Message);
        }
    }

    static int RemoveTortoiseSvn()
    {
        Out(L.T("TortoiseSVN 向けの設定を外す", "Removing the TortoiseSVN setup"));
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (key?.GetValue(RunName) != null)
                Do(L.T("自動起動の登録を削除", "remove the autostart entry"), () => key.DeleteValue(RunName));
        }
        if (Agent.Call(new() { ["cmd"] = "ping" }, 500) != null)
            Do(L.T("agent を終了", "stop the agent"), () => Agent.Call(new() { ["cmd"] = "quit" }));
        Out(L.T("キャッシュは残っています。消すときは svnop lock を実行してください",
                "The cache is left in place. Run svnop lock to delete it."));
        return 0;
    }

    // ---- msys2 ----

    sealed record Msys(string Root, string Bash, string HomeWin);

    static Msys FindMsys(Options o)
    {
        var root = o.Get("msys-root") ?? @"C:\msys64";
        var bash = Path.Combine(root, "usr", "bin", "bash.exe");
        if (!File.Exists(bash))
            throw new SvnOpException(L.T($"msys2 が見つかりません: {bash} (--msys-root で指定できます)",
                                         $"msys2 not found: {bash} (use --msys-root)"));
        // --home は検証用 (本物のホームを触らずに設定ファイルの書き換えを確かめる)
        var home = o.Get("home") ?? Bash(bash, "cygpath -w ~").Trim();
        if (home.Length == 0 || !Directory.Exists(home))
            throw new SvnOpException(L.T("msys2 のホームディレクトリを特定できません", "cannot determine the msys2 home directory"));
        return new Msys(root, bash, home);
    }

    /// <summary>msys2 のログインシェルでコマンドを実行して標準出力を返す</summary>
    static string Bash(string bash, string command)
    {
        var psi = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        psi.Environment["MSYSTEM"] = "MSYS";
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = errTask.Result;
        return output;
    }

    /// <summary>C:\a\b → /c/a/b (msys2 から見たパス)</summary>
    static string ToMsysPath(string win) =>
        "/" + char.ToLowerInvariant(win[0]) + win[2..].Replace('\\', '/');

    static int Msys2(Options o)
    {
        Out(L.T("msys2 の svn 向けの設定", "Setting up for msys2 svn"));
        var m = FindMsys(o);
        Out($"- msys2: {m.Root}  home: {m.HomeWin}");
        if (!File.Exists(Path.Combine(m.Root, "usr", "bin", "svn.exe")))
            Warn(L.T("msys2 に subversion が入っていません (pacman -S subversion)。入っていない間は TortoiseSVN の svn.exe が使われます",
                     "subversion is not installed in msys2 (pacman -S subversion). Until then TortoiseSVN's svn.exe is used"));
        var pinentry = Path.Combine(AppDir, "svn-op-pinentry.py");
        if (!File.Exists(pinentry))
            throw new SvnOpException(L.T($"{pinentry} がありません", $"{pinentry} not found"));
        var cfg = Config.Load();

        CheckPath(pinentry);
        CheckPath(m.Root);
        // 書き換える前に (--dry-run でも) マーカーの対応を確かめる
        foreach (var f in new[] { Path.Combine(m.HomeWin, ".gnupg", "gpg-agent.conf"), Path.Combine(m.HomeWin, ".bashrc") })
            CheckMarkers(ReadLines(f), f);

        // gpg-agent.conf: pinentry を差し替え、キャッシュ時間を足す。
        // 元の pinentry-program 行はファイル全体を退避する代わりに、svnop のブロック内にコメントで残す (解除時にその行だけ戻す)
        var gpgConf = Path.Combine(m.HomeWin, ".gnupg", "gpg-agent.conf");
        Do(L.T($"{gpgConf} の pinentry を svn-op-pinentry にする", $"point pinentry in {gpgConf} to svn-op-pinentry"), () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(gpgConf)!);
            var all = ReadLines(gpgConf);
            var originals = BlockLines(all, gpgConf).Where(l => l.StartsWith(OriginalMark)).ToList();
            var lines = StripBlock(all, gpgConf);
            originals.AddRange(lines.Where(l => l.TrimStart().StartsWith("pinentry-program")).Select(l => OriginalMark + l));
            originals.AddRange(LegacyBackupOriginals(gpgConf, originals));
            // 以前の手作業の設定 (マーカー無し) も取り除く
            lines.RemoveAll(l => l.TrimStart().StartsWith("pinentry-program") || l.StartsWith("# svnop:")
                                 || l.Trim() is "default-cache-ttl 3600" or "max-cache-ttl 28800");
            var block = new List<string> { Begin };
            block.AddRange(originals.Distinct());
            block.Add($"pinentry-program {ToMsysPath(pinentry)}");
            if (!lines.Any(l => l.TrimStart().StartsWith("default-cache-ttl")))
                block.Add("default-cache-ttl 3600");
            if (!lines.Any(l => l.TrimStart().StartsWith("max-cache-ttl")))
                block.Add("max-cache-ttl 28800");
            block.Add(End);
            WriteLines(gpgConf, [.. lines, .. block]);
        });

        // pinentry の設定
        // 署名を確認できた op.exe だけを pinentry に教える
        var opPath = cfg.FindOp() is { } found && Trust.IsSignedBy(found, "AgileBits") ? found : null;
        var svnopConf = Path.Combine(m.HomeWin, ".config", "svn-op", "config.json");
        Do(L.T($"{svnopConf} を作る", $"create {svnopConf}"), () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(svnopConf)!);
            var json = new JsonObject
            {
                ["svnop"] = ToMsysPath(Path.Combine(AppDir, "svnop.exe")),
                ["vault"] = cfg.Vault,
                ["fallback"] = "/usr/bin/pinentry-w32.exe",
            };
            if (opPath != null)
                json["op"] = ToMsysPath(opPath);  // FindOp がリンクを実体のパスにしている
            File.WriteAllText(svnopConf, json.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        });

        // .bashrc: msys2 のシェルでだけ gpg-agent を起動する
        var bashrc = Path.Combine(m.HomeWin, ".bashrc");
        var rootSlash = m.Root.Replace('\\', '/').TrimEnd('/') + "/";
        Do(L.T($"{bashrc} に gpg-agent の起動を足す", $"add gpg-agent startup to {bashrc}"), () =>
        {
            var lines = StripBlock(ReadLines(bashrc), bashrc);
            RemoveLegacyBashrc(lines);
            lines.AddRange([
                Begin,
                L.T("# msys2 の svn が gpg-agent (→ svnop → 1Password) を使えるよう、msys2 のシェルでだけ gpg-agent を起動する",
                    "# start gpg-agent (only in msys2 shells) so msys2 svn can use it (-> svnop -> 1Password)"),
                // パスはシェルのコードとして解釈されないよう単一引用符で囲む
                $"if [ \"$(cygpath -m / 2>/dev/null)\" = {BashQuote(rootSlash)} ]; then gpgconf --launch gpg-agent 2>/dev/null; fi",
                End,
            ]);
            WriteLines(bashrc, lines);
        });

        // msys2 の svn が初回から agent に問い合わせるよう、ユーザ名だけのキャッシュを作る
        if (opPath != null)
        {
            AuthCache.SimpleDir = Path.Combine(m.HomeWin, ".subversion", "auth", "svn.simple");
            try
            {
                var (created, skipped) = GpgInit(new OpClient(cfg), _dry);
                Out(L.T($"- {AuthCache.SimpleDir}: 作成 {created} 件 / 既存のためスキップ {skipped} 件",
                        $"- {AuthCache.SimpleDir}: created {created} / skipped (already present) {skipped}"));
            }
            catch (SvnOpException e)
            {
                Warn(L.T("ユーザ名キャッシュを作れませんでした (後で svnop gpg-init を実行してください): ",
                         "could not create username cache entries (run svnop gpg-init later): ") + e.Message);
            }
        }
        else
            Warn(L.T("op が無いので、ユーザ名キャッシュは作っていません (後で svnop gpg-init)",
                     "op not found; username cache entries were not created (run svnop gpg-init later)"));

        // 常駐する gpg-agent が出力のパイプを握ったままにならないよう、入出力は /dev/null に向ける
        // (--home での検証時は本物のホームの agent を触らない)
        if (o.Get("home") == null)
            Do(L.T("gpg-agent を起動し直す", "restart gpg-agent"),
               () => Bash(m.Bash, "gpgconf --kill gpg-agent; gpgconf --launch gpg-agent </dev/null >/dev/null 2>&1"));
        return 0;
    }

    static int RemoveMsys2(Options o)
    {
        Out(L.T("msys2 の svn 向けの設定を外す", "Removing the msys2 svn setup"));
        var m = FindMsys(o);
        var gpgConf = Path.Combine(m.HomeWin, ".gnupg", "gpg-agent.conf");
        var svnopConf = Path.Combine(m.HomeWin, ".config", "svn-op", "config.json");
        var bashrc = Path.Combine(m.HomeWin, ".bashrc");

        // svnop が設定していなければ何もしない (アンインストール時に、設定していない環境を触らないため)
        bool HasOurs(string path) => ReadLines(path).Any(l => l.Trim() == Begin || l.StartsWith("# svnop:"));
        if (!HasOurs(gpgConf) && !HasOurs(bashrc) && !File.Exists(svnopConf))
        {
            Out(L.T("- svnop の設定はありません", "- nothing set up by svnop"));
            return 0;
        }

        if (File.Exists(gpgConf))
            Do(L.T($"{gpgConf} から svnop の設定を外す (元の pinentry-program 行は戻す)",
                   $"remove the svnop settings from {gpgConf} (the original pinentry-program line is restored)"), () =>
            {
                var all = ReadLines(gpgConf);
                var originals = BlockLines(all, gpgConf).Where(l => l.StartsWith(OriginalMark))
                    .Select(l => l[OriginalMark.Length..]).ToList();
                var lines = StripBlock(all, gpgConf);
                if (!lines.Any(l => l.TrimStart().StartsWith("pinentry-program")))
                    lines.AddRange(originals);
                if (lines.All(string.IsNullOrWhiteSpace)) File.Delete(gpgConf);
                else WriteLines(gpgConf, lines);
                if (File.Exists(gpgConf + ".svnop-bak"))
                    File.Delete(gpgConf + ".svnop-bak");  // 旧版の退避ファイル (中の pinentry-program 行はブロックに移してある)
            });
        if (File.Exists(svnopConf))
            Do(L.T($"{svnopConf} を削除", $"delete {svnopConf}"), () => File.Delete(svnopConf));
        if (File.Exists(bashrc))
            Do(L.T($"{bashrc} から svnop の行を外す", $"remove the svnop lines from {bashrc}"), () =>
            {
                var lines = StripBlock(ReadLines(bashrc), bashrc);
                RemoveLegacyBashrc(lines);
                WriteLines(bashrc, lines);
            });
        if (o.Get("home") == null)
            Do(L.T("gpg-agent を終了", "stop gpg-agent"), () => Bash(m.Bash, "gpgconf --kill gpg-agent </dev/null >/dev/null 2>&1"));
        Out(L.T("~/.subversion のユーザ名キャッシュ (passtype=gpg-agent) は残しています",
                "Username cache entries in ~/.subversion (passtype=gpg-agent) are left in place."));
        return 0;
    }

    /// <summary>svnop のブロック内で、導入前の行を残しておく印</summary>
    const string OriginalMark = "# svnop-original: ";

    /// <summary>旧版が作った gpg-agent.conf.svnop-bak の pinentry-program 行 (まだブロックに無いものだけ)</summary>
    static IEnumerable<string> LegacyBackupOriginals(string gpgConf, List<string> already)
    {
        var bak = gpgConf + ".svnop-bak";
        if (!File.Exists(bak) || already.Count > 0)
            return [];
        return ReadLines(bak).Where(l => l.TrimStart().StartsWith("pinentry-program")).Select(l => OriginalMark + l).ToList();
    }

    /// <summary>bash の単一引用符で囲む (中の ' は '\'' にする)</summary>
    static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>設定ファイルに書くパスに改行などの制御文字が無いことを確かめる</summary>
    static void CheckPath(string path)
    {
        if (path.Any(char.IsControl))
            throw new SvnOpException(L.T("パスに制御文字が含まれています: ", "the path contains control characters: ") + path);
    }

    /// <summary>1Password の各 realm について GPG-Agent 形式のキャッシュを作る (既存ファイルには触れない)</summary>
    public static (int Created, int Skipped) GpgInit(OpClient op, bool dryRun)
    {
        int created = 0, skipped = 0;
        foreach (var item in op.List().OrderBy(i => i.Realm))
        {
            if (File.Exists(AuthCache.FileFor(item.Realm)))
            {
                skipped++;
                continue;
            }
            if (!dryRun)
                AuthCache.WriteGpgAgent(item.Realm, item.Username);
            created++;
        }
        return (created, skipped);
    }

    // ---- 設定ファイルの編集 (LF のまま、マーカーで囲んだ範囲だけを扱う) ----

    static List<string> ReadLines(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList() : [];

    static void WriteLines(string path, IEnumerable<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines).TrimStart('\n') + "\n", new UTF8Encoding(false));

    /// <summary>
    /// マーカーの対応を確かめる。開始と終了が交互に現れ、最後が閉じていなければ例外 (壊れたファイルを書き換えないため)
    /// </summary>
    static void CheckMarkers(List<string> lines, string path)
    {
        var inside = false;
        foreach (var l in lines)
        {
            if (l.Trim() == Begin)
            {
                if (inside) throw Broken(path);
                inside = true;
            }
            else if (l.Trim() == End)
            {
                if (!inside) throw Broken(path);
                inside = false;
            }
        }
        if (inside) throw Broken(path);
    }

    static SvnOpException Broken(string path) => new(L.T(
        $"{path} の svnop のマーカー (# >>> svnop >>> / # <<< svnop <<<) の対応が崩れているので変更しません。手で直してから実行してください",
        $"the svnop markers (# >>> svnop >>> / # <<< svnop <<<) in {path} do not match; not changing it. Fix it by hand and run again"));

    /// <summary>svnop のブロックを取り除いた行</summary>
    static List<string> StripBlock(List<string> lines, string path)
    {
        CheckMarkers(lines, path);
        var result = new List<string>();
        var inside = false;
        foreach (var l in lines)
        {
            if (l.Trim() == Begin) { inside = true; continue; }
            if (l.Trim() == End) { inside = false; continue; }
            if (!inside) result.Add(l);
        }
        while (result.Count > 0 && result[^1].Trim().Length == 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>svnop のブロックの中の行</summary>
    static List<string> BlockLines(List<string> lines, string path)
    {
        CheckMarkers(lines, path);
        var result = new List<string>();
        var inside = false;
        foreach (var l in lines)
        {
            if (l.Trim() == Begin) { inside = true; continue; }
            if (l.Trim() == End) { inside = false; continue; }
            if (inside) result.Add(l);
        }
        return result;
    }

    /// <summary>マーカー導入前に手作業で足した 2 行 (# svnop: のコメントと gpgconf --launch) を取り除く</summary>
    static void RemoveLegacyBashrc(List<string> lines)
    {
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("# svnop:"))
            {
                result.Add(lines[i]);
                continue;
            }
            if (i + 1 < lines.Count && lines[i + 1].Contains("gpgconf --launch gpg-agent"))
                i++;
            // 足したときに入れた直前の空行も取り除く
            if (result.Count > 0 && result[^1].Trim().Length == 0)
                result.RemoveAt(result.Count - 1);
        }
        lines.Clear();
        lines.AddRange(result);
    }
}
