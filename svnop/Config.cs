using System.Text.Json;

namespace SvnOp;

/// <summary>%APPDATA%\svnop\config.json の設定 (無ければ既定値)</summary>
public sealed class Config
{
    public string Op { get; set; } = "";              // 空なら PATH / WinGet Links から探す
    public string Vault { get; set; } = "";           // 空なら op の既定
    public string Tag { get; set; } = "svn";
    public string RealmField { get; set; } = "svn_realm";
    public bool AutoSubmit { get; set; } = true;      // agent: 入力後に OK を押す
    public bool NotifyOnFill { get; set; } = true;    // agent: 認証ダイアログに自動入力したらトレイ通知を出す

    /// <summary>
    /// 認証キャッシュ (%APPDATA%\Subversion) にパスワードを保存するか。
    /// keep: 保存する (TortoiseSVN の svn.exe も使える) / none: 保存しない (GUI は毎回自動入力) / 空: 未選択 (初回に聞く)
    /// </summary>
    public string CacheMode { get; set; } = "";
    /// <summary>keep のとき、agent の起動時 (ログオン時) に 1Password → キャッシュを同期する</summary>
    public bool SyncOnStart { get; set; } = true;
    /// <summary>表示言語: auto (Windows の表示言語) / ja / en。環境変数 SVNOP_LANG が優先</summary>
    public string Language { get; set; } = "auto";

    public const string Keep = "keep";
    public const string NoCache = "none";

    public bool KeepCache => CacheMode == Keep;
    public bool CacheModeChosen => CacheMode is Keep or NoCache;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "svnop");

    static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Config Load()
    {
        if (!File.Exists(FilePath))
            return new Config();
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpt) ?? new Config();
        }
        catch (JsonException e)
        {
            throw new SvnOpException($"{FilePath}: {e.Message}");
        }
    }

    /// <summary>キー名 (camelCase) で値を設定する。svnop config / トレイから使う</summary>
    public void Set(string key, string value)
    {
        bool Bool() => value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new SvnOpException(L.T($"true / false で指定してください: {value}", $"use true / false: {value}")),
        };
        switch (key)
        {
            case "vault": Vault = value; break;
            case "op": Op = value; break;
            case "tag": Tag = value; break;
            case "autoSubmit": AutoSubmit = Bool(); break;
            case "notifyOnFill": NotifyOnFill = Bool(); break;
            case "syncOnStart": SyncOnStart = Bool(); break;
            case "cacheMode":
                CacheMode = value is Keep or NoCache ? value
                    : throw new SvnOpException(L.T("keep か none で指定してください", "use keep or none"));
                break;
            case "language":
                Language = value is "auto" or "ja" or "en" ? value
                    : throw new SvnOpException(L.T("auto / ja / en で指定してください", "use auto / ja / en"));
                break;
            default:
                throw new SvnOpException(L.T("不明な設定です: ", "unknown setting: ") + key);
        }
    }

    public static readonly string[] Keys = ["cacheMode", "syncOnStart", "vault", "language", "notifyOnFill", "autoSubmit", "op", "tag"];

    public string Get(string key) => key switch
    {
        "vault" => Vault,
        "op" => Op,
        "tag" => Tag,
        "autoSubmit" => AutoSubmit ? "true" : "false",
        "notifyOnFill" => NotifyOnFill ? "true" : "false",
        "syncOnStart" => SyncOnStart ? "true" : "false",
        "cacheMode" => CacheMode,
        "language" => Language,
        _ => throw new SvnOpException(L.T("不明な設定です: ", "unknown setting: ") + key),
    };

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpt));
    }

    /// <summary>op.exe の場所。見つからなければ null</summary>
    public string? FindOp()
    {
        if (Op.Length > 0)
            return File.Exists(Op) ? Op : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0)
                continue;
            var p = Path.Combine(dir.Trim(), "op.exe");
            if (File.Exists(p))
                return p;
        }
        var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "Microsoft", "WinGet", "Links", "op.exe");
        return File.Exists(links) ? links : null;
    }

    public string ResolveOp() => FindOp() ?? throw new SvnOpException(L.T(
        "op.exe が見つかりません。1Password CLI を導入するか (winget install AgileBits.1Password.CLI)、config.json の op に場所を書いてください",
        "op.exe not found. Install the 1Password CLI (winget install AgileBits.1Password.CLI) or set \"op\" in config.json"));
}

public sealed class SvnOpException(string message) : Exception(message);
