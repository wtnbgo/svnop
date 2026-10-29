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
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Sanitize(msg)}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>改行などの制御文字をエスケープする (realm やユーザー名でログの行を偽装されないように)</summary>
    public static string Sanitize(string s)
    {
        if (!s.Any(char.IsControl))
            return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsControl(c) ? $"\\x{(int)c:x2}" : c.ToString());
        return sb.ToString();
    }

    Agent()
    {
        _op = new OpClient(_cfg);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "svnop agent (Subversion × 1Password)",
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildMenu();
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        new Thread(PipeLoop) { IsBackground = true, Name = "svnop-pipe" }.Start();
        Log($"agent start pid={Environment.ProcessId} pipe={PipeName} tsvn={AuthDialog.TsvnBinDir} cacheMode={_cfg.CacheMode}");
        if (!_cfg.CacheModeChosen)
            _ui.Post(_ => ChooseCacheMode(), null);  // 初回: メッセージループが回ってから方針を聞く
        else if (_cfg.KeepCache && _cfg.SyncOnStart)
            // ログオン直後は 1Password がまだ起動・解錠されていないことがあるので、失敗したら間を置いて数回やり直す
            _ = RunSync(notify: false, retries: 5);
        StartWatcher();
    }

    // ---- トレイメニュー ----

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var sync = new ToolStripMenuItem(T("1Password から同期 (sync)", "Sync from 1Password"), null, (_, _) => _ = RunSync(notify: true, retries: 0));
        menu.Items.Add(sync);
        menu.Items.Add(T("キャッシュを消す (lock)", "Clear the cache (lock)"), null, (_, _) => RunLock());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildSettingsMenu());
        menu.Items.Add(T("ヘルプ (README)", "Help (README)"), null, (_, _) => OpenReadme());
        menu.Items.Add(T("ログを開く", "Open log"), null, (_, _) => Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("終了", "Exit"), null, (_, _) => Quit());
        // キャッシュを保存しない方針のときは同期できない
        menu.Opening += (_, _) =>
        {
            ReloadConfig();
            sync.Enabled = _cfg.KeepCache;
        };
        return menu;
    }

    /// <summary>手で編集された config.json を反映する (vault 等の文字列は OpClient が同じオブジェクトを見ている)</summary>
    void ReloadConfig()
    {
        Config fresh;
        try { fresh = Config.Load(); }
        catch (SvnOpException e) { Log(e.Message); return; }
        _cfg.CacheMode = fresh.CacheMode;
        _cfg.SyncOnStart = fresh.SyncOnStart;
        _cfg.NotifyOnFill = fresh.NotifyOnFill;
        _cfg.AutoSubmit = fresh.AutoSubmit;
        _cfg.Language = fresh.Language;
        _cfg.Vault = fresh.Vault;
    }

    ToolStripMenuItem BuildSettingsMenu()
    {
        var root = new ToolStripMenuItem(T("設定", "Settings"));
        var vault = new ToolStripMenuItem { Enabled = false };

        // 認証キャッシュの方針 (排他)
        var cacheHead = new ToolStripMenuItem(T("認証キャッシュにパスワードを:", "Passwords in the credential cache:")) { Enabled = false };
        var keep = new ToolStripMenuItem(T("保存する (svn.exe も使える)", "Save (svn.exe works too)"), null, (_, _) => SetCacheMode(Config.Keep));
        var none = new ToolStripMenuItem(T("保存しない (GUI は毎回自動入力)", "Do not save (GUI is auto-filled every time)"), null, (_, _) => SetCacheMode(Config.NoCache));
        var syncOnStart = new ToolStripMenuItem(T("    ログオン時に 1Password から同期する", "    Sync from 1Password at logon"), null, (_, _) =>
        {
            _cfg.SyncOnStart = !_cfg.SyncOnStart;
            _cfg.Save();
            Log($"setting: syncOnStart = {_cfg.SyncOnStart}");
        });

        ToolStripMenuItem Toggle(string ja, string en, Func<bool> get, Action<bool> set, string key) =>
            new(T(ja, en), null, (_, _) =>
            {
                set(!get());
                _cfg.Save();
                Log($"setting: {key} = {get()}");
            });
        var notify = Toggle("自動入力したら通知する", "Notify when auto-filled", () => _cfg.NotifyOnFill, v => _cfg.NotifyOnFill = v, "notifyOnFill");
        var submit = Toggle("自動入力の後に OK を押す", "Press OK after auto-fill", () => _cfg.AutoSubmit, v => _cfg.AutoSubmit = v, "autoSubmit");

        // 表示言語
        var lang = new ToolStripMenuItem(T("表示言語", "Language"));
        var langItems = new[] { ("auto", T("自動 (Windows に合わせる)", "Auto (follow Windows)")), ("ja", "日本語"), ("en", "English") }
            .Select(x => (Key: x.Item1, Item: new ToolStripMenuItem(x.Item2, null, (_, _) => SetLanguage(x.Item1)))).ToList();
        foreach (var (_, item) in langItems)
            lang.DropDownItems.Add(item);

        root.DropDownItems.AddRange([
            vault, new ToolStripSeparator(),
            cacheHead, keep, syncOnStart, none, new ToolStripSeparator(),
            notify, submit, new ToolStripSeparator(),
            lang,
            new ToolStripMenuItem(T("設定ファイルを開く", "Open config file"), null, (_, _) =>
            {
                var path = Path.Combine(Config.Dir, "config.json");
                if (!File.Exists(path))
                    _cfg.Save();
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }),
        ]);
        root.DropDownOpening += (_, _) =>
        {
            ReloadConfig();
            vault.Text = T("ボールト: ", "Vault: ") + (_cfg.Vault.Length > 0 ? _cfg.Vault : T("(op の既定)", "(op default)"));
            keep.Checked = _cfg.KeepCache;
            none.Checked = _cfg.CacheMode == Config.NoCache;
            syncOnStart.Checked = _cfg.SyncOnStart;
            syncOnStart.Enabled = _cfg.KeepCache;
            notify.Checked = _cfg.NotifyOnFill;
            submit.Checked = _cfg.AutoSubmit;
            foreach (var (key, item) in langItems)
                item.Checked = _cfg.Language == key;
        };
        return root;
    }

    /// <summary>初回起動時に方針を聞く (閉じられたら次回また聞く)</summary>
    void ChooseCacheMode()
    {
        using var form = new CacheModeForm(_cfg);
        if (form.ShowDialog() != DialogResult.OK)
        {
            Log("cache mode not chosen (will ask again next time)");
            return;
        }
        _cfg.CacheMode = form.Mode;
        _cfg.SyncOnStart = form.SyncOnStart;
        _cfg.Save();
        Log($"setting: cacheMode = {_cfg.CacheMode}, syncOnStart = {_cfg.SyncOnStart}");
        if (_cfg.KeepCache && _cfg.SyncOnStart)
            _ = RunSync(notify: true, retries: 5);
    }

    void SetCacheMode(string mode)
    {
        if (_cfg.CacheMode == mode)
            return;
        _cfg.CacheMode = mode;
        _cfg.Save();
        Interlocked.Increment(ref _cacheGen);  // 取得中の同期を取り消す
        Log($"setting: cacheMode = {mode}");
        if (mode == Config.NoCache)
        {
            var count = ManagedList.Load().Valid().Count(kv => File.Exists(kv.Key));
            if (count > 0 && MessageBox.Show(
                    T($"svnop が書いた認証キャッシュが {count} 件あります。今消しますか?\n(消さない場合も、以後 svnop はキャッシュに書き込みません)",
                      $"There are {count} cache entries written by svnop. Delete them now?\n(Either way, svnop will no longer write to the cache.)"),
                    "svnop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RunLock();
        }
        else if (MessageBox.Show(T("今すぐ 1Password からキャッシュを同期しますか?", "Sync the cache from 1Password now?"),
                                 "svnop", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _ = RunSync(notify: true, retries: 0);
    }

    void SetLanguage(string lang)
    {
        _cfg.Language = lang;
        _cfg.Save();
        L.Ja = L.Detect();
        // メニューの文言を作り直す
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
        Log($"setting: language = {lang}");
    }

    void OpenReadme()
    {
        var dir = AppContext.BaseDirectory;
        var file = Path.Combine(dir, L.Ja ? "README.md" : "README.en.md");
        var target = File.Exists(file) ? file : "https://github.com/wtnbgo/svnop#readme";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    // ---- キャッシュの同期 / 削除 ----

    /// <summary>キャッシュへの書き込み (sync) と削除 (lock) を排他する</summary>
    readonly SemaphoreSlim _cacheLock = new(1, 1);
    /// <summary>lock や方針変更のたびに増やす。1Password からの取得中にこれが変わった同期は書き込まずに取り消す</summary>
    int _cacheGen;

    async Task RunSync(bool notify, int retries)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!_cfg.KeepCache)
            {
                Log("sync skipped: cache mode is not keep");
                return;
            }
            var gen = Volatile.Read(ref _cacheGen);
            try
            {
                var items = await WithOp(() => _op.ListWithPasswords());
                CacheSync.SyncResult r;
                await _cacheLock.WaitAsync();
                try
                {
                    // 取得している間に lock / 方針変更があったら書き戻さない
                    if (!_cfg.KeepCache || gen != Volatile.Read(ref _cacheGen))
                    {
                        Log("sync cancelled: the cache was locked or the cache mode changed while fetching");
                        return;
                    }
                    r = CacheSync.Sync(items, dryRun: false, force: false, Log);
                }
                finally
                {
                    _cacheLock.Release();
                }
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

    async void RunLock()
    {
        CacheSync.LockResult r;
        await _cacheLock.WaitAsync();
        try
        {
            Interlocked.Increment(ref _cacheGen);  // 取得中の同期を取り消す
            r = CacheSync.Lock(force: false, Log);
        }
        finally
        {
            _cacheLock.Release();
        }
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
            if (_cfg.NotifyOnFill)
                _tray.ShowBalloonTip(3000, "svnop", T("1Password から入力しました", "Filled from 1Password")
                                     + $"\n{dlg.Realm}\n({item.Username})", ToolTipIcon.Info);
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
                // キャッシュへの保存は TortoiseSVN に任せる (保存する方針なら「認証を保存」付きで送るので、成功したときだけ保存される)
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
        if (!dlg.StillValid())
        {
            Log($"not filled: the dialog changed or closed while waiting: {dlg.Realm} pid={dlg.Pid}");
            return;
        }
        _lastFill[(dlg.Pid, dlg.Realm)] = DateTime.Now;
        // キャッシュを保存する方針なら「認証を保存」を付けたまま送る (認証に成功したときだけ TortoiseSVN が保存する)
        dlg.Fill(user, password, saveAuth: _cfg.KeepCache, _cfg.AutoSubmit);
    }

    // ---- 名前付きパイプ (1 行 JSON の要求 → 1 行 JSON の応答) ----
    //
    // 信頼モデル: 同じユーザーのプロセスは、1Password の承認が有効な間、agent 経由でパスワードを取得できる
    // (gpg-agent のキャッシュと同じ水準)。他のユーザーとネットワークからは使えないようにし、
    // 書き込み (save) は agent 側で利用者に確認する。不要なら config.json の allowPipe を false にする。

    const int MaxRequestBytes = 64 * 1024;
    const int MaxInstances = 4;
    static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);

    void PipeLoop()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl,
                                                  AccessControlType.Allow));
        // ネットワーク (SMB) 経由の接続は拒否する
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                                                  PipeAccessRights.FullControl, AccessControlType.Deny));
        var first = true;
        while (!_quit.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // 最初のインスタンスは FirstPipeInstance で作る: 同じ名前のパイプを他のプロセスが先に作っていたら失敗する
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, options, 0, 0, security);
            }
            catch (Exception e) when (first && e is IOException or UnauthorizedAccessException)
            {
                Log("pipe: the pipe name is already in use by another process; the pipe is disabled: " + e.Message);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 同時接続数の上限など。少し待って作り直す
                Log("pipe: " + e.Message);
                Thread.Sleep(500);
                continue;
            }
            first = false;
            try
            {
                pipe.WaitForConnectionAsync(_quit.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException e) { Log("pipe: " + e.Message); pipe.Dispose(); continue; }
            // 接続ごとに別タスクで処理する (1 つの接続で他の要求を止められないように)
            _ = Task.Run(() => Serve(pipe));
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                string? line;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_quit.Token))
                {
                    cts.CancelAfter(IoTimeout);
                    line = await ReadLineAsync(pipe, MaxRequestBytes, cts.Token);
                }
                if (line == null)
                    return;
                var response = HandleRequest(line, ClientDescription(pipe));
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_quit.Token))
                {
                    cts.CancelAfter(IoTimeout);
                    await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(response.ToJsonString() + "\n"), cts.Token);
                    await pipe.FlushAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { Log("pipe: request timed out"); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Log("pipe: " + e.Message);
            }
        }
    }

    /// <summary>ログ用: 接続してきたプロセス</summary>
    static string ClientDescription(NamedPipeServerStream pipe) =>
        Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)
            ? $"pid={pid} {Path.GetFileName(Win32.ImagePath(pid) ?? "?")}"
            : "pid=?";

    /// <summary>改行までを読む (上限を超えたら例外)。接続が切れたら null</summary>
    static async Task<string?> ReadLineAsync(Stream s, int max, CancellationToken ct)
    {
        using var buf = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0)
                return buf.Length == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(buf.ToArray());
            if (buf.Length >= max)
                throw new IOException("request too large");
            buf.WriteByte(one[0]);
        }
    }

    JsonObject HandleRequest(string line, string client)
    {
        var req = JsonNode.Parse(line)?.AsObject() ?? [];
        var cmd = Str(req, "cmd");
        var realm = Str(req, "realm");
        var user = Str(req, "user");
        try
        {
            // allowPipe=false のときは ping / quit (agent --status / --stop) だけを受け付ける
            if (cmd is "find" or "save" && !_cfg.AllowPipe)
                return new JsonObject { ["error"] = "disabled (allowPipe=false)" };
            if (cmd is "find" or "save" && (realm.Length is 0 or > 1024 || user.Length is 0 or > 256))
                return new JsonObject { ["error"] = "invalid realm or user" };
            switch (cmd)
            {
                case "find":
                {
                    var item = WithOp(() => _op.Find(realm, user)).GetAwaiter().GetResult();
                    Log($"pipe find: {realm} ({user}) found={item != null} from {client}");
                    return item == null
                        ? new JsonObject { ["found"] = false }
                        : new JsonObject { ["found"] = true, ["id"] = item.Id, ["password"] = item.Password };
                }
                case "save":
                {
                    var pw = Str(req, "password");
                    if (pw.Length is 0 or > 4096)
                        return new JsonObject { ["error"] = "invalid password" };
                    // 書き込みは利用者に確認する (同じユーザーの任意のプロセスが 1Password を書き換えられないように)
                    var ok = false;
                    _ui.Send(_ => ok = MessageBox.Show(
                        T($"Subversion のパスワードを 1Password に記録しますか?\n\n{realm}\nユーザー: {user}\n\n(要求元: {client})",
                          $"Record this Subversion password in 1Password?\n\n{realm}\nUser: {user}\n\n(requested by: {client})"),
                        "svnop", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes, null);
                    if (!ok)
                    {
                        Log($"pipe save declined: {realm} ({user}) from {client}");
                        return new JsonObject { ["error"] = "declined" };
                    }
                    var id = WithOp(() => _op.Save(realm, user, pw, _op.Find(realm, user))).GetAwaiter().GetResult();
                    _list = null;
                    Log($"pipe save: {realm} ({user}) {id} from {client}");
                    return new JsonObject { ["ok"] = true, ["id"] = id };
                }
                case "ping":
                    return new JsonObject { ["ok"] = true, ["pid"] = Environment.ProcessId };
                case "quit":
                    Log($"pipe quit from {client}");
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

    static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>
    /// 動いている agent に 1 行 JSON を送って応答を受け取る (動いていなければ null)。
    /// 偽の agent に秘密を渡さないよう、接続先が同じユーザーの、同じフォルダにある svnop の exe であることを確かめる。
    /// </summary>
    public static JsonObject? Call(JsonObject request, int connectTimeoutMs = 2000, int responseTimeoutMs = 300_000)
    {
        // CurrentUserOnly: パイプの所有者が現在のユーザーでなければ接続しない
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                                                   PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { pipe.Connect(connectTimeoutMs); }
        catch (TimeoutException) { return null; }
        catch (UnauthorizedAccessException)
        {
            throw new SvnOpException(T("svnop のパイプが別のユーザーのものなので接続しません", "the svnop pipe belongs to another user; not connecting"));
        }
        // パスワードを送る / 受け取る要求のときは、相手が svnop の agent かを確かめる (ping / quit は秘密を扱わない)
        if (request["cmd"]?.GetValue<string>() is "find" or "save")
            VerifyServer(pipe);
        using var cts = new CancellationTokenSource(responseTimeoutMs);
        try
        {
            pipe.WriteAsync(new UTF8Encoding(false).GetBytes(request.ToJsonString() + "\n"), cts.Token).AsTask().GetAwaiter().GetResult();
            pipe.FlushAsync(cts.Token).GetAwaiter().GetResult();
            var line = ReadLineAsync(pipe, 1024 * 1024, cts.Token).GetAwaiter().GetResult();
            return line == null ? null : JsonNode.Parse(line)?.AsObject();
        }
        catch (OperationCanceledException)
        {
            throw new SvnOpException(T("agent からの応答がありません", "no response from the agent"));
        }
    }

    static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!Win32.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            throw new SvnOpException(T("agent のプロセスを確認できません", "cannot identify the agent process"));
        var image = Win32.ImagePath(pid);
        var dir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var ok = image != null
                 && string.Equals(Path.GetDirectoryName(image), dir, StringComparison.OrdinalIgnoreCase)
                 && Path.GetFileName(image).ToLowerInvariant() is "svnopw.exe" or "svnop.exe";
        if (!ok)
            throw new SvnOpException(T($"パイプの相手が svnop の agent ではありません: {image ?? "?"}",
                                       $"the pipe server is not the svnop agent: {image ?? "?"}"));
    }
}
