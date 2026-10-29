using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SvnOp;

/// <summary>1Password 上の SVN 認証情報 1 件</summary>
public sealed record SvnItem(string Id, string Realm, string Username, string? Password, JsonObject? Raw);

/// <summary>
/// op CLI 経由の 1Password アクセス。アイテムの形は msys2 の svn-op-pinentry と共通:
/// Login カテゴリ、タグ svn、タイトル "SVN &lt;user&gt; @ &lt;realm&gt;"、フィールド username / password / svn_realm。
/// op の承認は呼び出し元プロセス単位なので、同じプロセスから呼ぶ限り 2 回目以降は承認不要 (10 分無操作で失効)。
/// </summary>
public sealed class OpClient(Config cfg)
{
    readonly string _op = cfg.ResolveOp();
    readonly string _mapPath = Path.Combine(Config.Dir, "realm-map.json");

    public static string Title(string realm, string user) => $"SVN {user} @ {realm}";

    static string Key(string realm, string user) => realm + "\n" + user;

    /// <summary>op を実行して stdout を返す。失敗時は null と stderr</summary>
    (string? Out, string Err) Run(IEnumerable<string> args, string? stdin = null, bool withVault = true)
    {
        var psi = new ProcessStartInfo(_op)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (withVault && cfg.Vault.Length > 0)
        {
            psi.ArgumentList.Add("--vault");
            psi.ArgumentList.Add(cfg.Vault);
        }
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new SvnOpException(L.T("op を起動できません: ", "cannot start op: ") + _op);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new SvnOpException(L.T("op を起動できません: ", "cannot start op: ") + $"{_op} ({e.Message})");
        }
        using var _ = p;
        // op は stdin を読みながら出力するので、先に読み取りを始めておかないと大きな入力でパイプが詰まる
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin != null)
            p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = outTask.Result;
        p.WaitForExit();
        var err = errTask.Result.Trim();
        return p.ExitCode == 0 ? (output, err) : (null, err);
    }

    string RunOrThrow(IEnumerable<string> args, string? stdin = null, bool withVault = true)
    {
        var (output, err) = Run(args, stdin, withVault);
        return output ?? throw new SvnOpException(L.T("op が失敗しました: ", "op failed: ") + Summarize(err));
    }

    /// <summary>
    /// op のエラー出力は、ログや画面・パイプの応答に流れるので、最初の 1 行だけを長さを切って使う
    /// (万一の秘密の混入や、制御文字によるログの偽装を避けるため)
    /// </summary>
    static string Summarize(string err)
    {
        var first = err.Split('\n', 2)[0].Trim();
        first = new string(first.Where(c => !char.IsControl(c)).ToArray());
        return first.Length > 200 ? first[..200] + "…" : first;
    }

    /// <summary>アクセスできるボールトの名前一覧</summary>
    public List<string> VaultNames()
    {
        var arr = JsonNode.Parse(RunOrThrow(["vault", "list", "--format", "json"], withVault: false))?.AsArray() ?? [];
        return arr.Select(v => v?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList();
    }

    JsonObject? Get(string reference)
    {
        var (output, _) = Run(["item", "get", reference, "--reveal", "--format", "json"]);
        return output == null ? null : JsonNode.Parse(output)?.AsObject();
    }

    SvnItem? ToItem(JsonObject it)
    {
        // id 指定の item get はアーカイブ済みも返すので除外する
        if (it["state"]?.GetValue<string>() == "ARCHIVED")
            return null;
        string? realm = null, user = null, pw = null;
        foreach (var f in it["fields"]?.AsArray() ?? [])
        {
            var label = f?["label"]?.GetValue<string>();
            var value = f?["value"]?.GetValue<string>();
            if (label == cfg.RealmField) realm = value;
            else if (f?["purpose"]?.GetValue<string>() == "USERNAME") user = value;
            else if (f?["purpose"]?.GetValue<string>() == "PASSWORD") pw = value;
        }
        if (realm == null || user == null)
            return null;
        return new SvnItem(it["id"]!.GetValue<string>(), realm, user, pw, it);
    }

    /// <summary>realm とユーザ名で引く (op 呼び出しは 1 回)</summary>
    public SvnItem? Find(string realm, string user)
    {
        var map = LoadMap();
        var reference = map.GetValueOrDefault(Key(realm, user)) ?? Title(realm, user);
        var it = Get(reference);
        var item = it == null ? null : ToItem(it);
        if (item == null || item.Realm != realm || item.Username != user)
            return null;
        if (map.GetValueOrDefault(Key(realm, user)) != item.Id)
        {
            map[Key(realm, user)] = item.Id;
            SaveMap(map);
        }
        return item;
    }

    /// <summary>タグ付きアイテムの一覧 (タイトルから realm / user を取り出す。パスワードは取らない)</summary>
    public List<SvnItem> List()
    {
        var arr = JsonNode.Parse(RunOrThrow(["item", "list", "--tags", cfg.Tag, "--format", "json"]))?.AsArray() ?? [];
        var result = new List<SvnItem>();
        foreach (var it in arr)
        {
            var title = it?["title"]?.GetValue<string>() ?? "";
            var id = it?["id"]?.GetValue<string>() ?? "";
            if (!title.StartsWith("SVN "))
                continue;
            var at = title.IndexOf(" @ ", StringComparison.Ordinal);
            if (at < 0)
                continue;
            result.Add(new SvnItem(id, title[(at + 3)..], title[4..at], null, null));
        }
        return result;
    }

    /// <summary>タグ付きアイテムをパスワード込みで全件取る (list と get を 1 回ずつ。get は一覧の JSON を stdin で渡す)</summary>
    public List<SvnItem> ListWithPasswords()
    {
        var list = RunOrThrow(["item", "list", "--tags", cfg.Tag, "--format", "json"]);
        if (JsonNode.Parse(list)?.AsArray() is not { Count: > 0 })
            return [];
        var output = RunOrThrow(["item", "get", "-", "--reveal", "--format", "json"], list);
        // 結果は JSON オブジェクトの連続 (配列ではない)
        var result = new List<SvnItem>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(output),
                                        new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;
            if (JsonNode.Parse(ref reader) is JsonObject it && ToItem(it) is { } item)
                result.Add(item);
        }
        return result;
    }

    /// <summary>作成または更新。パスワードはコマンドラインに出さず stdin の JSON で渡す</summary>
    public string Save(string realm, string user, string password, SvnItem? existing)
    {
        string id;
        if (existing?.Raw != null)
        {
            var it = (JsonObject)existing.Raw.DeepClone();
            foreach (var f in it["fields"]?.AsArray() ?? [])
                if (f?["purpose"]?.GetValue<string>() == "PASSWORD")
                    f["value"] = password;
            RunOrThrow(["item", "edit", existing.Id, "--format", "json"], it.ToJsonString());
            id = existing.Id;
        }
        else
        {
            var tmpl = new JsonObject
            {
                ["title"] = Title(realm, user),
                ["category"] = "LOGIN",
                ["tags"] = new JsonArray(cfg.Tag),
                ["fields"] = new JsonArray(
                    new JsonObject { ["id"] = "username", ["type"] = "STRING", ["purpose"] = "USERNAME", ["label"] = "username", ["value"] = user },
                    new JsonObject { ["id"] = "password", ["type"] = "CONCEALED", ["purpose"] = "PASSWORD", ["label"] = "password", ["value"] = password },
                    new JsonObject { ["id"] = "svn_realm", ["type"] = "STRING", ["label"] = cfg.RealmField, ["value"] = realm }),
            };
            var created = JsonNode.Parse(RunOrThrow(["item", "create", "--format", "json", "-"], tmpl.ToJsonString()));
            id = created!["id"]!.GetValue<string>();
        }
        var map = LoadMap();
        map[Key(realm, user)] = id;
        SaveMap(map);
        return id;
    }

    Dictionary<string, string> LoadMap()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_mapPath)) ?? [];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return [];
        }
    }

    void SaveMap(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(_mapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
