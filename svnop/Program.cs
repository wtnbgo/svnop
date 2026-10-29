using System.Text;
using static SvnOp.L;

namespace SvnOp;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        // コンソールが無い起動のされ方 (インストーラから非表示で実行など) でも落ちないようにする
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch (IOException) { }
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            var detail = args.Length > 1 ? Help.For(args[1]) : null;
            Console.WriteLine(detail ?? Help.Usage);
            return args.Length == 0 ? 1 : 0;
        }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine("svnop " + Help.Version);
            return 0;
        }
        try
        {
            var opts = Options.Parse(args[1..]);
            if (opts.Has("help"))
            {
                Console.WriteLine(Help.For(args[0]) ?? Help.Usage);
                return 0;
            }
            if (opts.Get("auth-dir") is { } authDir)
                AuthCache.SimpleDir = Path.GetFullPath(authDir);
            return args[0] switch
            {
                "setup" => Setup.Run(opts),
                "config" => ConfigCmd(opts),
                "realm" => await RealmCmd(opts),
                "add" => await AddCmd(opts),
                "find" => await FindCmd(opts),
                "list" => ListCmd(),
                "cache" => CacheCmd(opts),
                "import" => ImportCmd(opts),
                "sync" => SyncCmd(opts),
                "lock" => LockCmd(opts),
                "status" => StatusCmd(),
                "dialogs" => DialogsCmd(opts),
                "agent" => AgentCmd(opts),
                "gpg-init" => GpgInitCmd(opts),
                "call" => CallCmd(),
                _ => throw new SvnOpException(T("不明なコマンドです: ", "unknown command: ") + args[0]
                                              + T("  (svnop help で一覧)", "  (see svnop help)")),
            };
        }
        catch (Exception e) when (e is SvnOpException or HttpRequestException or System.Net.Sockets.SocketException
                                       or UriFormatException or TaskCanceledException or OperationCanceledException
                                       or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine(T("エラー: ", "Error: ") + e.Message);
            return 2;
        }
    }

    /// <summary>svnop config / svnop config KEY / svnop config KEY VALUE</summary>
    static int ConfigCmd(Options o)
    {
        var cfg = Config.Load();
        switch (o.Positional.Count)
        {
            case 0:
                foreach (var k in Config.Keys)
                    Console.WriteLine($"{k} = {cfg.Get(k)}");
                Console.Error.WriteLine(Path.Combine(Config.Dir, "config.json"));
                return 0;
            case 1:
                Console.WriteLine(cfg.Get(o.Positional[0]));
                return 0;
            case 2:
                cfg.Set(o.Positional[0], o.Positional[1]);
                cfg.Save();
                Console.WriteLine($"{o.Positional[0]} = {cfg.Get(o.Positional[0])}");
                if (Agent.Call(new() { ["cmd"] = "ping" }, 300) != null)
                    Console.WriteLine(T("(常駐中の agent には次にメニューを開いたときか、次の起動時に反映されます)",
                                        "(the running agent picks this up the next time its menu opens, or at its next start)"));
                return 0;
            default:
                throw new SvnOpException(T("使い方: svnop config [キー [値]]", "usage: svnop config [key [value]]"));
        }
    }

    static async Task<int> RealmCmd(Options o)
    {
        if (o.Positional.Count == 0)
            throw new SvnOpException(T("URL を指定してください", "specify a URL"));
        var rc = 0;
        foreach (var url in o.Positional)
        {
            try
            {
                var r = await RealmProbe.ProbeAsync(url, o.Has("insecure"));
                Console.WriteLine(url);
                Console.WriteLine("  realm: " + (r.Realm ?? T("(なし)", "(none)")));
                Console.WriteLine("  " + r.Note);
                if (r.Realm == null) rc = 1;
            }
            catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException
                                           or OperationCanceledException or SvnOpException)
            {
                Console.WriteLine(url);
                Console.WriteLine(T("  接続できませんでした: ", "  could not connect: ") + e.Message);
                rc = 1;
            }
        }
        return rc;
    }

    /// <summary>--realm 指定 / realm 文字列そのもの / URL を調べる、のいずれかで realm を得る</summary>
    static async Task<string> ResolveRealm(Options o, string target)
    {
        if (o.Get("realm") is { } given)
            return given;
        if (target.StartsWith('<'))
            return target;
        var r = await RealmProbe.ProbeAsync(target, o.Has("insecure"));
        Console.WriteLine("realm: " + (r.Realm ?? T("(なし)", "(none)")) + "  - " + r.Note);
        return r.Realm ?? throw new SvnOpException(T("realm を特定できませんでした (--realm で直接指定できます)",
                                                     "could not determine the realm (use --realm to specify it)"));
    }

    static async Task<int> AddCmd(Options o)
    {
        if (o.Positional.Count != 1)
            throw new SvnOpException(T("URL を 1 つ指定してください", "specify one URL"));
        var realm = await ResolveRealm(o, o.Positional[0]);

        // ユーザ名: 指定 > ローカルキャッシュ > 入力
        var cached = AuthCache.ReadAll().FirstOrDefault(e => e.Realm == realm)?.Username;
        var label = T("ユーザー名", "User name");
        var user = o.Get("user") ?? Prompt(cached != null ? $"{label} [{cached}]: " : $"{label}: ", cached);
        if (string.IsNullOrEmpty(user))
            throw new SvnOpException(T("ユーザー名が空です", "the user name is empty"));

        var op = new OpClient(Config.Load());
        var existing = op.Find(realm, user);
        if (existing != null)
        {
            Console.WriteLine(T("1Password に登録済みです: ", "Already in 1Password: ") + $"{OpClient.Title(realm, user)} ({existing.Id})");
            if (!Confirm(T("パスワードを更新しますか? [y/N]: ", "Update the password? [y/N]: ")))
                return 0;
        }
        var pw = ReadPassword(T("パスワード: ", "Password: "));
        if (pw.Length == 0)
            throw new SvnOpException(T("パスワードが空です", "the password is empty"));
        if (ReadPassword(T("もう一度: ", "Again: ")) != pw)
            throw new SvnOpException(T("パスワードが一致しません", "the passwords do not match"));
        var id = op.Save(realm, user, pw, existing);
        Console.WriteLine((existing != null ? T("更新しました: ", "Updated: ") : T("登録しました: ", "Added: "))
                          + $"{OpClient.Title(realm, user)} ({id})");
        // キャッシュを保存する方針なら、TortoiseSVN の svn.exe からもすぐ使えるようキャッシュにも書く (明示的に登録した値なので既存より優先)
        if (!o.Has("no-cache") && Config.Load().KeepCache)
            CacheSync.Sync([new SvnItem(id, realm, user, pw, null)], dryRun: false, force: true, Console.WriteLine);
        return 0;
    }

    static async Task<int> FindCmd(Options o)
    {
        if (o.Positional.Count != 1)
            throw new SvnOpException(T("URL か realm を 1 つ指定してください", "specify one URL or realm"));
        var realm = await ResolveRealm(o, o.Positional[0]);
        var op = new OpClient(Config.Load());
        if (o.Get("user") is { } user)
        {
            var item = op.Find(realm, user);
            Console.WriteLine(item == null
                ? T("登録なし", "not registered")
                : T("登録あり: ", "registered: ") + $"{OpClient.Title(realm, user)} ({item.Id}) "
                  + (string.IsNullOrEmpty(item.Password) ? T("パスワードなし", "no password") : T("パスワードあり", "has password")));
            return item != null ? 0 : 1;
        }
        var hits = op.List().Where(i => i.Realm == realm).ToList();
        foreach (var i in hits)
            Console.WriteLine(T("登録あり: ", "registered: ") + $"user={i.Username} ({i.Id})");
        if (hits.Count == 0)
            Console.WriteLine(T("登録なし", "not registered"));
        return hits.Count > 0 ? 0 : 1;
    }

    static int ListCmd()
    {
        var items = new OpClient(Config.Load()).List();
        foreach (var i in items.OrderBy(i => i.Realm).ThenBy(i => i.Username))
            Console.WriteLine($"{i.Realm}\t{i.Username}\t{i.Id}");
        Console.Error.WriteLine(T($"{items.Count} 件", $"{items.Count} items"));
        return 0;
    }

    static int CacheCmd(Options o)
    {
        var host = o.Positional.Count > 0 ? RealmProbe.Prefix(new Uri(o.Positional[0])) : null;
        var entries = AuthCache.ReadAll().Where(e => host == null || e.Realm.StartsWith(host + " ")).ToList();
        foreach (var e in entries.OrderBy(e => e.Realm))
            Console.WriteLine($"{e.Realm}\t{e.Username}\t{e.PassType}");
        Console.Error.WriteLine(T($"{entries.Count} 件", $"{entries.Count} entries"));
        return 0;
    }

    static int ImportCmd(Options o)
    {
        var all = AuthCache.ReadAll(withPassword: true).Where(e => e.Username != null).ToList();
        var entries = all.Where(e => e.Password != null).OrderBy(e => e.Realm).ToList();
        var skipped = all.Count - entries.Count;
        if (o.Get("realm") is { } realmFilter)
            entries = entries.Where(e => e.Realm == realmFilter).ToList();
        if (entries.Count == 0)
        {
            Console.WriteLine(T("取り込めるキャッシュがありません", "nothing to import"));
            return 1;
        }

        var op = new OpClient(Config.Load());
        var registered = op.List().Select(i => (i.Realm, i.Username)).ToHashSet();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var mark = registered.Contains((e.Realm, e.Username!)) ? T("登録済", "exists") : T("未登録", "new   ");
            Console.WriteLine($"[{i + 1,3}] {mark}  {e.Realm}  ({e.Username})");
        }
        if (skipped > 0)
            Console.WriteLine(T($"(パスワードを取り出せない {skipped} 件は除外: gpg-agent 形式や他ユーザで暗号化されたもの)",
                                $"({skipped} entries without a readable password were skipped: gpg-agent style or encrypted by another user)"));
        if (o.Has("dry-run"))
            return 0;

        var update = o.Has("update");
        List<CacheEntry> selected;
        if (o.Has("all") || o.Get("realm") != null)
            selected = entries;
        else
        {
            var answer = Prompt(T("取り込む番号 (例: 1,3-5 / all / 空で中止): ", "Numbers to import (e.g. 1,3-5 / all / empty to cancel): "), "") ?? "";
            if (answer.Length == 0)
                return 0;
            selected = answer == "all" ? entries : ParseSelection(answer, entries.Count).Select(n => entries[n - 1]).ToList();
        }

        var managed = o.Has("adopt") ? ManagedList.Load() : null;
        int created = 0, updated = 0, same = 0;
        foreach (var e in selected)
        {
            var exists = registered.Contains((e.Realm, e.Username!));
            // 1Password に保存した / 1Password と値が同じと確かめたものだけを管理下に置く
            // (スキップしたものを管理下に置くと、ローカルにしかない新しいパスワードを lock で消すおそれがある)
            var inSync = false;
            if (exists && !update)
            {
                Console.WriteLine(T("スキップ (登録済み。値は比べていないので管理下には置きません): ",
                                    "skipped (exists; values not compared, so not put under management): ") + $"{e.Realm} ({e.Username})");
            }
            else
            {
                var existing = exists ? op.Find(e.Realm, e.Username!) : null;
                if (existing != null && existing.Password == e.Password)
                    same++;
                else
                {
                    op.Save(e.Realm, e.Username!, e.Password!, existing);
                    if (existing != null) updated++; else created++;
                    Console.WriteLine((existing != null ? T("更新: ", "updated: ") : T("登録: ", "added: ")) + $"{e.Realm} ({e.Username})");
                }
                inSync = true;
            }
            // --adopt: 1Password と同じ内容のキャッシュとして svnop の管理下に置く (lock で消える)
            if (managed != null && inSync && AuthCache.IsCacheFileFor(e.File, e.Realm))
                managed.Files[e.File] = new ManagedList.Entry(e.Realm, e.Username!, AuthCache.HashOf(File.ReadAllBytes(e.File)));
        }
        managed?.Save();
        Console.WriteLine(T($"登録 {created} 件 / 更新 {updated} 件 / 変更なし {same} 件",
                            $"added {created} / updated {updated} / unchanged {same}"));
        return 0;
    }

    static IEnumerable<int> ParseSelection(string s, int max)
    {
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b))
            {
                for (var n = a; n <= b; n++)
                    if (n >= 1 && n <= max) yield return n;
            }
            else if (int.TryParse(part, out var n) && n >= 1 && n <= max)
                yield return n;
            else
                throw new SvnOpException(T("番号の指定が正しくありません: ", "invalid number: ") + part);
        }
    }

    static int SyncCmd(Options o)
    {
        var dryRun = o.Has("dry-run");
        var cfg = Config.Load();
        if (cfg.CacheMode == Config.NoCache && !o.Has("force"))
            throw new SvnOpException(T("キャッシュを保存しない設定です。変えるときは svnop config cacheMode keep (今回だけなら --force)",
                                       "the cache mode is \"none\" (do not save). Change it with svnop config cacheMode keep (or use --force once)"));
        var items = new OpClient(cfg).ListWithPasswords();
        var r = CacheSync.Sync(items, dryRun, o.Has("force"), Console.WriteLine);
        Console.WriteLine(r.Summary(dryRun));
        return 0;
    }

    static int LockCmd(Options o)
    {
        var r = CacheSync.Lock(o.Has("force"), Console.WriteLine);
        Console.WriteLine(r.Summary);
        return r.Kept > 0 ? 1 : 0;
    }

    static int StatusCmd()
    {
        var managed = ManagedList.Load();
        foreach (var (file, e) in managed.Files.OrderBy(kv => kv.Value.Realm))
        {
            var state = !File.Exists(file) ? T("なし", "missing")
                      : ManagedList.Unchanged(file, e) ? T("同期済み", "synced") : T("変更あり", "changed");
            Console.WriteLine($"{state}\t{e.Realm}\t{e.User}");
        }
        Console.Error.WriteLine(T($"管理中 {managed.Files.Count} 件 (キャッシュ: {AuthCache.SimpleDir})",
                                  $"{managed.Files.Count} managed entries (cache: {AuthCache.SimpleDir})"));
        return 0;
    }

    static int GpgInitCmd(Options o)
    {
        if (o.Get("auth-dir") == null)
            throw new SvnOpException(T("--auth-dir で msys2 svn の svn.simple を指定してください (例: C:\\Users\\<you>\\.subversion\\auth\\svn.simple)",
                                       "specify msys2 svn's svn.simple with --auth-dir (e.g. C:\\Users\\<you>\\.subversion\\auth\\svn.simple)"));
        var dry = o.Has("dry-run");
        var (created, skipped) = Setup.GpgInit(new OpClient(Config.Load()), dry);
        Console.WriteLine(dry ? T($"作成予定 {created} 件 / 既存のためスキップ {skipped} 件", $"would create {created} / skipped (already present) {skipped}")
                              : T($"作成 {created} 件 / 既存のためスキップ {skipped} 件", $"created {created} / skipped (already present) {skipped}"));
        return 0;
    }

    /// <summary>
    /// stdin の 1 行 JSON を agent に中継し、応答を 1 行で返す。agent が動いていなければ終了コード 3。
    /// msys2 (cygwin) の Python は Windows の名前付きパイプを開けないので、svn-op-pinentry はこれを経由する。
    /// </summary>
    static int CallCmd()
    {
        var req = System.Text.Json.Nodes.JsonNode.Parse(Console.ReadLine() ?? "")?.AsObject()
                  ?? throw new SvnOpException(T("要求が空です", "empty request"));
        var res = Agent.Call(req, 1000);
        if (res == null)
            return 3;
        Console.WriteLine(res.ToJsonString());
        return 0;
    }

    static int AgentCmd(Options o)
    {
        if (o.Has("run"))
            return Agent.Run();
        if (o.Has("stop"))
        {
            var r = Agent.Call(new() { ["cmd"] = "quit" });
            Console.WriteLine(r == null ? T("agent は動いていません", "the agent is not running") : T("agent を終了しました", "the agent was stopped"));
            return 0;
        }
        var ping = Agent.Call(new() { ["cmd"] = "ping" }, 500);
        if (ping != null)
        {
            Console.WriteLine(T($"agent は動いています (pid {ping["pid"]})", $"the agent is running (pid {ping["pid"]})"));
            return 0;
        }
        if (o.Has("status"))
        {
            Console.WriteLine(T("agent は動いていません", "the agent is not running"));
            return 1;
        }
        var pid = Agent.StartDetached();
        Console.WriteLine(T($"agent を起動しました (pid {pid})", $"the agent was started (pid {pid})"));
        return 0;
    }

    /// <summary>調査用。--fill USER を付けると、stdin のパスワードを最初に見つかった認証ダイアログに入れて OK を押す</summary>
    static int DialogsCmd(Options o)
    {
        foreach (var line in AuthDialog.Dump())
            Console.WriteLine(line);
        if (o.Get("fill") is { } user)
        {
            var dlg = AuthDialog.FindAll().FirstOrDefault() ?? throw new SvnOpException(T("認証ダイアログがありません", "no auth dialog found"));
            dlg.Fill(user, Console.ReadLine() ?? "", saveAuth: false, submit: true);
            Console.WriteLine(T("入力しました: ", "filled: ") + dlg.Realm);
        }
        return 0;
    }

    // ---- 入力 ----

    static string? Prompt(string label, string? defaultValue)
    {
        Console.Write(label);
        var s = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(s) ? defaultValue : s;
    }

    static bool Confirm(string label)
    {
        Console.Write(label);
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    static string ReadPassword(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
                break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
            }
            else if (!char.IsControl(k.KeyChar))
                sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

/// <summary>"--name value" 形式のオプション、値を取らないフラグ、位置引数</summary>
sealed class Options
{
    static readonly HashSet<string> Flags =
        ["all", "update", "adopt", "dry-run", "force", "run", "stop", "status", "no-cache", "remove", "no-autostart", "help", "insecure"];

    public List<string> Positional { get; } = [];
    readonly Dictionary<string, string> _named = [];
    readonly HashSet<string> _flags = [];

    public string? Get(string name) => _named.GetValueOrDefault(name);

    public bool Has(string flag) => _flags.Contains(flag);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "-h" || (args[i].StartsWith("--") && Flags.Contains(args[i][2..])))
                o._flags.Add(args[i] == "-h" ? "help" : args[i][2..]);
            else if (args[i].StartsWith("--"))
            {
                if (i + 1 >= args.Length)
                    throw new SvnOpException(args[i] + L.T(" に値がありません", " needs a value"));
                o._named[args[i][2..]] = args[++i];
            }
            else
                o.Positional.Add(args[i]);
        }
        return o;
    }
}
