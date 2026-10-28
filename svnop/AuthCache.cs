using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SvnOp;

/// <summary>svn.simple キャッシュの 1 件。Password は ReadAll(withPassword: true) のときだけ入る</summary>
public sealed record CacheEntry(string File, string Realm, string? Username, string? PassType, string? Password = null);

/// <summary>%APPDATA%\Subversion\auth\svn.simple の読み書き (hash-dump 形式)</summary>
public static class AuthCache
{
    /// <summary>--auth-dir / SVNOP_AUTH_DIR で差し替え可能 (検証用)</summary>
    public static string SimpleDir { get; set; } =
        Environment.GetEnvironmentVariable("SVNOP_AUTH_DIR") is { Length: > 0 } d ? d : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion", "auth", "svn.simple");

    /// <summary>キャッシュのファイル名は realm 文字列の MD5 (小文字 16 進)</summary>
    public static string FileFor(string realm) =>
        Path.Combine(SimpleDir, Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(realm))));

    public static IEnumerable<CacheEntry> ReadAll(bool withPassword = false)
    {
        if (!Directory.Exists(SimpleDir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(SimpleDir))
        {
            Dictionary<string, string> h;
            try { h = ReadHash(File.ReadAllBytes(file)); }
            catch (FormatException) { continue; }
            if (!h.TryGetValue("svn:realmstring", out var realm))
                continue;
            var passType = h.GetValueOrDefault("passtype");
            string? pw = null;
            if (withPassword && h.TryGetValue("password", out var stored))
                pw = passType switch
                {
                    Wincrypt.PassType => Wincrypt.Decrypt(stored),
                    "simple" => stored,
                    _ => null,
                };
            yield return new CacheEntry(file, realm, h.GetValueOrDefault("username"), passType, pw);
        }
    }

    /// <summary>wincrypt 形式で書き込み、書いた内容の SHA-256 を返す</summary>
    public static (string File, string Hash) WriteWincrypt(string realm, string user, string password)
    {
        var h = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["passtype"] = Wincrypt.PassType,
            ["password"] = Wincrypt.Encrypt(password),
            ["svn:realmstring"] = realm,
            ["username"] = user,
        };
        var data = WriteHash(h);
        Directory.CreateDirectory(SimpleDir);
        var file = FileFor(realm);
        File.WriteAllBytes(file, data);
        return (file, HashOf(data));
    }

    /// <summary>
    /// GPG-Agent 形式 (ユーザ名と passtype だけ。パスワードは gpg-agent → pinentry が都度取る) で書き込む。
    /// msys2 の svn はこのファイルが無いと agent に問い合わせず端末で聞いてくるので、事前に作っておく。
    /// </summary>
    public static string WriteGpgAgent(string realm, string user)
    {
        var h = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["passtype"] = "gpg-agent",
            ["svn:realmstring"] = realm,
            ["username"] = user,
        };
        Directory.CreateDirectory(SimpleDir);
        var file = FileFor(realm);
        File.WriteAllBytes(file, WriteHash(h));
        return file;
    }

    public static string HashOf(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>"K len\nkey\nV len\nvalue\n" の繰り返しと "END\n"</summary>
    public static Dictionary<string, string> ReadHash(byte[] data)
    {
        var result = new Dictionary<string, string>();
        var pos = 0;
        string Line()
        {
            var nl = Array.IndexOf(data, (byte)'\n', pos);
            if (nl < 0) throw new FormatException();
            var s = Encoding.UTF8.GetString(data, pos, nl - pos);
            pos = nl + 1;
            return s;
        }
        string Chunk(char tag)
        {
            var head = Line();
            if (head.Length < 3 || head[0] != tag || head[1] != ' ') throw new FormatException();
            var len = int.Parse(head[2..]);
            if (pos + len > data.Length) throw new FormatException();
            var s = Encoding.UTF8.GetString(data, pos, len);
            pos += len + 1;
            return s;
        }
        while (pos < data.Length)
        {
            if (data[pos] == 'E')
                break;
            var k = Chunk('K');
            result[k] = Chunk('V');
        }
        return result;
    }

    static byte[] WriteHash(IEnumerable<KeyValuePair<string, string>> h)
    {
        using var ms = new MemoryStream();
        void Put(char tag, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            ms.Write(Encoding.ASCII.GetBytes($"{tag} {b.Length}\n"));
            ms.Write(b);
            ms.WriteByte((byte)'\n');
        }
        foreach (var (k, v) in h)
        {
            Put('K', k);
            Put('V', v);
        }
        ms.Write("END\n"u8);
        return ms.ToArray();
    }
}

/// <summary>svnop が書いた (sync / import --adopt) キャッシュファイルの記録。lock はここに載ったものだけを消す</summary>
public sealed class ManagedList
{
    public sealed record Entry(string Realm, string User, string Hash);

    static string PathOf => System.IO.Path.Combine(Config.Dir, "managed.json");

    public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ManagedList Load()
    {
        try
        {
            var files = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(PathOf));
            return new ManagedList { Files = new(files ?? [], StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new ManagedList();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(Files, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>ファイルが svnop が書いたときのままか</summary>
    public static bool Unchanged(string file, Entry e) =>
        File.Exists(file) && AuthCache.HashOf(File.ReadAllBytes(file)) == e.Hash;
}
