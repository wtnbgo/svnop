using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using static SvnOp.L;

namespace SvnOp;

/// <summary>
/// 常駐部分。
/// - TortoiseSVN の認証ダイアログを監視し、1Password の値を入れて OK を押す
/// - 1Password に無い / 1Password の値で失敗したときは入力ウインドウを出し、入力を 1Password に記録する
/// - 名前付きパイプで 1Password への問い合わせを受け付ける (msys2 の svn-op-pinentry 用)
/// op を呼ぶのは常にこのプロセスなので、1Password の承認は 10 分無操作で失効するまで 1 回で済む。
/// </summary>
sealed class Agent : ApplicationContext
{
    public static string PipeName => "svnop-" + Environment.UserName;

    /// <summary>同じプロセス・同じ realm のダイアログがこの時間内に再び出たら、入力した値で失敗したとみなす</summary>
    static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(60);

    readonly Config _cfg = Config.Load();
    readonly OpClient _op;
    readonly SemaphoreSlim _opLock = new(1, 1);
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
    readonly HashSet<IntPtr> _handled = [];
    readonly Dictionary<(uint Pid, string Realm), DateTime> _lastFill = [];
    readonly SynchronizationContext _ui;
    readonly CancellationTokenSource _quit = new();
    List<SvnItem>? _list;
    DateTime _listAt;

    static readonly string LogPath = Path.Combine(Config.Dir, "agent.log");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    Agent()
    {
        _op = new OpClient(_cfg);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var menu = new ContextMenuStrip();
        menu.Items.Add(T("1Password から同期 (sync)", "Sync from 1Password"), null, (_, _) => _ = RunSync(notify: true, retries: 0));
        menu.Items.Add(T("キャッシュを消す (lock)", "Clear the cache (lock)"), null, (_, _) => RunLock());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("ログを開く", "Open log"), null, (_, _) => Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true }));
        menu.Items.Add(T("終了", "Exit"), null, (_, _) => Quit());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "svnop agent (Subversion × 1Password)",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        new Thread(PipeLoop) { IsBackground = true, Name = "svnop-pipe" }.Start();
        Log($"agent start pid={Environment.ProcessId} pipe={PipeName} tsvn={AuthDialog.TsvnBinDir}");
        // ログオン直後は 1Password がまだ起動・解錠されていないことがあるので、失敗したら間を置いて数回やり直す
        if (_cfg.SyncOnStart)
            _ = RunSync(notify: false, retries: 5);
        StartWatcher();
    }

    // ---- キャッシュの同期 / 削除 ----

    async Task RunSync(bool notify, int retries)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var r = await WithOp(() => CacheSync.Sync(_op.ListWithPasswords(), dryRun: false, force: false, Log));
                Log("sync: " + r.Summary(false));
                if (notify || r.Written > 0 || r.Skipped > 0)
                    _tray.ShowBalloonTip(5000, "svnop", T("1Password から同期しました: ", "Synced from 1Password: ") + r.Summary(false), ToolTipIcon.Info);
                return;
            }
            catch (Exception e) when (e is SvnOpException or IOException)
            {
                Log($"sync failed (attempt {attempt + 1}): {e.Message}");
                if (attempt >= retries || _quit.IsCancellationRequested)
                {
                    _tray.ShowBalloonTip(5000, "svnop", T("1Password からの同期に失敗しました: ", "Sync from 1Password failed: ") + e.Message, ToolTipIcon.Warning);
                    return;
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), _quit.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // ---- キャッシュの監視: svn.exe などが新しく保存した認証情報を 1Password に記録する ----

    FileSystemWatcher? _watcher;
    readonly System.Windows.Forms.Timer _scanDelay = new() { Interval = 2000 };
    /// <summary>記録を断ったファイル (パス → 内容のハッシュ)。同じ内容ならもう聞かない</summary>
    readonly Dictionary<string, string> _declined = new(StringComparer.OrdinalIgnoreCase);
    bool _scanning;

    void StartWatcher()
    {
        Directory.CreateDirectory(AuthCache.SimpleDir);
        _scanDelay.Tick += (_, _) => { _scanDelay.Stop(); _ = ScanCache(); };
        _watcher = new FileSystemWatcher(AuthCache.SimpleDir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        // 書き込み途中で読まないよう、最後の変更から少し待ってまとめて調べる
        FileSystemEventHandler kick = (_, _) => _ui.Post(_ => { _scanDelay.Stop(); _scanDelay.Start(); }, null);
        _watcher.Created += kick;
        _watcher.Changed += kick;
        _watcher.EnableRaisingEvents = true;
    }

    async Task ScanCache()
    {
        if (_scanning)
        {
            _scanDelay.Start();
            return;
        }
        _scanning = true;
        try
        {
            var managed = ManagedList.Load();
            foreach (var e in AuthCache.ReadAll(withPassword: true))
            {
                if (e.Username == null || e.Password == null)
                    continue;
                var hash = AuthCache.HashOf(File.ReadAllBytes(e.File));
                // svnop 自身が書いたまま / 一度断った内容のままなら対象外
                if (managed.Files.TryGetValue(e.File, out var m) && m.Hash == hash)
                    continue;
                if (_declined.TryGetValue(e.File, out var d) && d == hash)
                    continue;

                SvnItem? item;
                try { item = await WithOp(() => _op.Find(e.Realm, e.Username)); }
                catch (Exception ex) when (ex is SvnOpException or IOException)
                {
                    Log("cache watch: 1Password lookup failed: " + ex.Message);
                    continue;
                }
                if (item?.Password != e.Password)
                {
                    var what = item == null ? T("1Password に登録がありません", "not in 1Password") : T("1Password の値と違います", "differs from 1Password");
                    var answer = MessageBox.Show(
                        T($"Subversion が新しい認証情報を保存しました ({what})。\n\n{e.Realm}\nユーザー: {e.Username}\n\n1Password に記録しますか?",
                          $"Subversion saved new credentials ({what}).\n\n{e.Realm}\nUser: {e.Username}\n\nRecord them in 1Password?"),
                        "svnop", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly);
                    if (answer != DialogResult.Yes)
                    {
                        _declined[e.File] = hash;
                        Log($"cache watch: declined: {e.Realm} ({e.Username})");
                        continue;
                    }
                    try
                    {
                        var id = await WithOp(() => _op.Save(e.Realm, e.Username, e.Password, item));
                        _list = null;
                        Log($"cache watch: {(item != null ? "updated" : "added")} in 1Password: {e.Realm} ({e.Username}) {id}");
                    }
                    catch (Exception ex) when (ex is SvnOpException or IOException)
                    {
                        Log("cache watch: recording in 1Password failed: " + ex.Message);
                        continue;
                    }
                }
                // 1Password と同じ内容になったので svnop の管理下に置く (lock で消える)
                managed = ManagedList.Load();
                managed.Files[e.File] = new ManagedList.Entry(e.Realm, e.Username, hash);
                managed.Save();
                Log($"cache watch: now managed: {e.Realm} ({e.Username})");
            }
        }
        finally
        {
            _scanning = false;
        }
    }

    void RunLock()
    {
        var r = CacheSync.Lock(force: false, Log);
        Log("lock: " + r.Summary);
        _tray.ShowBalloonTip(5000, "svnop", T("キャッシュを消しました: ", "Cache cleared: ") + r.Summary, ToolTipIcon.Info);
    }

    /// <summary>コンソールを持たない子プロセスとして常駐を始める (隣に svnopw.exe があればそれを使う)。pid を返す</summary>
    public static int? StartDetached()
    {
        var self = Environment.ProcessPath!;
        var w = Path.Combine(Path.GetDirectoryName(self)!, "svnopw.exe");
        var psi = File.Exists(w)
            ? new ProcessStartInfo(w) { UseShellExecute = false }
            : new ProcessStartInfo(self, "agent --run") { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(psi)?.Id;
    }

    public static int Run()
    {
        using var mutex = new Mutex(true, "Local\\svnop-agent", out var created);
        if (!created)
        {
            Console.Error.WriteLine(T("agent は既に動いています", "the agent is already running"));
            return 1;
        }
        // Main は async (MTA) なので、WinForms 用に STA スレッドを立てる
        var ui = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Run(new Agent());
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return 0;
    }

    void Quit()
    {
        Log("agent exit");
        _quit.Cancel();
        _timer.Stop();
        _watcher?.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    async Task<T> WithOp<T>(Func<T> f)
    {
        await _opLock.WaitAsync();
        try { return await Task.Run(f); }
        finally { _opLock.Release(); }
    }

    // ---- 認証ダイアログ ----

    void Poll()
    {
        _handled.RemoveWhere(h => !Win32.IsWindow(h));
        foreach (var dlg in AuthDialog.FindAll())
        {
            if (!_handled.Add(dlg.Hwnd))
                continue;
            var key = (dlg.Pid, dlg.Realm);
            var failed = _lastFill.TryGetValue(key, out var at) && DateTime.Now - at < RetryWindow;
            _ = HandleDialog(dlg, failed);
        }
    }

    /// <summary>realm の登録を探す。ユーザ名はローカルキャッシュにあればそれを優先</summary>
    SvnItem? Lookup(string realm)
    {
        if (_list == null || DateTime.Now - _listAt > ListCacheTtl)
        {
            _list = _op.List();
            _listAt = DateTime.Now;
        }
        var candidates = _list.Where(i => i.Realm == realm).ToList();
        if (candidates.Count == 0)
            return null;
        var cachedUser = AuthCache.ReadAll().FirstOrDefault(e => e.Realm == realm)?.Username;
        var user = candidates.Any(c => c.Username == cachedUser) ? cachedUser! : candidates[0].Username;
        return _op.Find(realm, user);
    }

    async Task HandleDialog(AuthDialog dlg, bool failed)
    {
        SvnItem? item = null;
        string? error = null;
        try
        {
            item = await WithOp(() => Lookup(dlg.Realm));
        }
        catch (Exception e) when (e is SvnOpException or IOException)
        {
            error = e.Message;
            Log("1Password lookup failed: " + e.Message);
        }
        if (!Win32.IsWindow(dlg.Hwnd))
            return;

        if (item is { Password: { Length: > 0 } pw } && !failed)
        {
            Fill(dlg, item.Username, pw);
            Log($"filled: {dlg.Realm} ({item.Username}) pid={dlg.Pid}");
            return;
        }

        var message = failed ? T("直前に入力した値で認証に失敗しました。正しいパスワードを入力してください (1Password も更新します)",
                                 "Authentication failed with the value just entered. Enter the correct password (1Password is updated too).")
                    : error != null ? T("1Password を参照できませんでした: ", "Could not read 1Password: ") + error
                    : T("1Password に登録がありません。入力すると 1Password に記録します",
                        "Not registered in 1Password. What you enter here is recorded in 1Password.");
        Log($"prompt: {dlg.Realm} failed={failed} found={item != null}");
        using var form = new PromptForm(dlg.Realm, message, item?.Username
                                        ?? AuthCache.ReadAll().FirstOrDefault(e => e.Realm == dlg.Realm)?.Username);
        if (form.ShowDialog() != DialogResult.OK || form.User.Length == 0 || form.Password.Length == 0)
        {
            Log($"cancelled: {dlg.Realm} (TortoiseSVN dialog left as is)");
            return;
        }
        if (form.Save)
        {
            try
            {
                var id = await WithOp(() =>
                {
                    var existing = item?.Username == form.User ? item : _op.Find(dlg.Realm, form.User);
                    var id = _op.Save(dlg.Realm, form.User, form.Password, existing);
                    _list = null;
                    Log($"{(existing != null ? "updated" : "added")} in 1Password: {dlg.Realm} ({form.User}) {id}");
                    return id;
                });
                // TortoiseSVN の svn.exe (コンソール) からも使えるよう、キャッシュにも書く
                CacheSync.Sync([new SvnItem(id, dlg.Realm, form.User, form.Password, null)], dryRun: false, force: true, Log);
            }
            catch (Exception e) when (e is SvnOpException or IOException)
            {
                Log("recording in 1Password failed: " + e.Message);
                MessageBox.Show(T("1Password への記録に失敗しました:\n", "Recording in 1Password failed:\n") + e.Message,
                                "svnop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (Win32.IsWindow(dlg.Hwnd))
            Fill(dlg, form.User, form.Password);
    }

    void Fill(AuthDialog dlg, string user, string password)
    {
        _lastFill[(dlg.Pid, dlg.Realm)] = DateTime.Now;
        dlg.Fill(user, password, _cfg.SaveAuth, _cfg.AutoSubmit);
    }

    // ---- 名前付きパイプ (1 行 JSON の要求 → 1 行 JSON の応答) ----

    void PipeLoop()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl,
                                                  AccessControlType.Allow));
        while (!_quit.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                pipe.WaitForConnectionAsync(_quit.Token).GetAwaiter().GetResult();
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var line = reader.ReadLine();
                var response = line == null ? null : HandleRequest(line);
                if (response != null)
                {
                    var bytes = new UTF8Encoding(false).GetBytes(response.ToJsonString() + "\n");
                    pipe.Write(bytes);
                    pipe.Flush();
                    pipe.WaitForPipeDrain();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Log("pipe: " + e.Message);
            }
        }
    }

    JsonObject HandleRequest(string line)
    {
        var req = JsonNode.Parse(line)?.AsObject() ?? [];
        var cmd = req["cmd"]?.GetValue<string>();
        var realm = req["realm"]?.GetValue<string>() ?? "";
        var user = req["user"]?.GetValue<string>() ?? "";
        try
        {
            switch (cmd)
            {
                case "find":
                {
                    var item = WithOp(() => _op.Find(realm, user)).GetAwaiter().GetResult();
                    Log($"pipe find: {realm} ({user}) found={item != null}");
                    return item == null
                        ? new JsonObject { ["found"] = false }
                        : new JsonObject { ["found"] = true, ["id"] = item.Id, ["password"] = item.Password };
                }
                case "save":
                {
                    var pw = req["password"]?.GetValue<string>() ?? "";
                    var id = WithOp(() => _op.Save(realm, user, pw, _op.Find(realm, user))).GetAwaiter().GetResult();
                    _list = null;
                    Log($"pipe save: {realm} ({user}) {id}");
                    return new JsonObject { ["ok"] = true, ["id"] = id };
                }
                case "ping":
                    return new JsonObject { ["ok"] = true, ["pid"] = Environment.ProcessId };
                case "quit":
                    _ui.Post(_ => Quit(), null);
                    return new JsonObject { ["ok"] = true };
                default:
                    return new JsonObject { ["error"] = "unknown command" };
            }
        }
        catch (Exception e) when (e is SvnOpException or IOException)
        {
            Log($"pipe {cmd} failed: {e.Message}");
            return new JsonObject { ["error"] = e.Message };
        }
    }

    /// <summary>動いている agent に 1 行 JSON を送って応答を受け取る (動いていなければ null)</summary>
    public static JsonObject? Call(JsonObject request, int timeoutMs = 2000)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        try { pipe.Connect(timeoutMs); }
        catch (TimeoutException) { return null; }
        var bytes = new UTF8Encoding(false).GetBytes(request.ToJsonString() + "\n");
        pipe.Write(bytes);
        pipe.Flush();
        var line = new StreamReader(pipe, new UTF8Encoding(false)).ReadLine();
        return line == null ? null : JsonNode.Parse(line)?.AsObject();
    }
}
