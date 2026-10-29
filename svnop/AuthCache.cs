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

    /// <summary>キャッシュ 1 ファイルの大きさの上限 (これを超えるものは壊れているとみなして読まない)</summary>
    const int MaxCacheFileBytes = 256 * 1024;

    /// <summary>
    /// file が「realm から計算したキャッシュファイル」(SimpleDir 直下、名前は realm の MD5) そのものか。
    /// managed.json の内容を信用して任意のパスを消さないための確認
    /// </summary>
    public static bool IsCacheFileFor(string file, string realm)
    {
        try
        {
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(FileFor(realm)), StringComparison.OrdinalIgnoreCase))
                return false;
            return !File.Exists(file) || !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>file が今のキャッシュの場所 (SimpleDir) の直下か</summary>
    public static bool InCurrentDir(string file)
    {
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(SimpleDir).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static IEnumerable<CacheEntry> ReadAll(bool withPassword = false)
    {
        if (!Directory.Exists(SimpleDir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(SimpleDir))
        {
            Dictionary<string, string> h;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxCacheFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                h = ReadHash(File.ReadAllBytes(file));
            }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { continue; }
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

    /// <summary>
    /// "K len\nkey\nV len\nvalue\n" の繰り返しと "END\n"。
    /// 長さは 0 以上で残りのバイト数以内、値の後ろは改行、最後は END、を確かめ、外れていれば FormatException
    /// </summary>
    public static Dictionary<string, string> ReadHash(byte[] data)
    {
        var result = new Dictionary<string, string>();
        var pos = 0;
        string Line()
        {
            var nl = Array.IndexOf(data, (byte)'\n', pos);
            if (nl < 0) throw new FormatException("missing newline");
            var s = Encoding.UTF8.GetString(data, pos, nl - pos);
            pos = nl + 1;
            return s;
        }
        string Chunk(char tag)
        {
            var head = Line();
            if (head.Length < 3 || head[0] != tag || head[1] != ' ')
                throw new FormatException("bad header");
            if (!int.TryParse(head.AsSpan(2), System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out var len))
                throw new FormatException("bad length");
            if (len > data.Length - pos - 1 || data[pos + len] != (byte)'\n')
                throw new FormatException("bad value");
            var s = Encoding.UTF8.GetString(data, pos, len);
            pos += len + 1;
            return s;
        }
        while (true)
        {
            if (pos >= data.Length)
                throw new FormatException("missing END");
            if (data[pos] == (byte)'E')
            {
                if (Line() != "END")
                    throw new FormatException("bad END");
                return result;
            }
            var k = Chunk('K');
            result[k] = Chunk('V');
        }
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

    /// <summary>
    /// 読み込み。今のキャッシュの場所で「realm から計算したファイル名」と一致する項目だけを使う
    /// (管理記録を細工されても、キャッシュ以外のファイルを消さないため。別の場所の記録はそのまま保存し直す)
    /// </summary>
    public static ManagedList Load()
    {
        Dictionary<string, Entry>? files;
        try
        {
            files = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(PathOf));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new ManagedList();
        }
        var list = new ManagedList();
        foreach (var (file, e) in files ?? [])
        {
            if (e is { Realm: { Length: > 0 }, User: not null, Hash: { Length: 64 } })
                list.Files[file] = e;
        }
        return list;
    }

    /// <summary>今のキャッシュの場所にある、正しい名前のファイルの項目だけ</summary>
    public IEnumerable<KeyValuePair<string, Entry>> Valid() =>
        Files.Where(kv => AuthCache.IsCacheFileFor(kv.Key, kv.Value.Realm));

    public void Save()
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(Files, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>ファイルが svnop が書いたときのままか</summary>
    public static bool Unchanged(string file, Entry e) =>
        File.Exists(file) && AuthCache.HashOf(File.ReadAllBytes(file)) == e.Hash;
}
