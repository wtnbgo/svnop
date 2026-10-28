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
    public bool SaveAuth { get; set; } = false;       // agent: TortoiseSVN の「認証を保存」を残すか
    public bool SyncOnStart { get; set; } = true;     // agent: 起動時 (ログオン時) に 1Password → キャッシュを同期する

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
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpt) ?? new Config();
    }

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
