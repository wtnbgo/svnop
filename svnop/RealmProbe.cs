using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SvnOp;

/// <summary>realm 調査の結果。Realm が null なら NeedsPassword=false の理由が Note に入る</summary>
public sealed record ProbeResult(string? Realm, string Note);

/// <summary>
/// URL から Subversion の realm 文字列を調べる。認証情報は送らない。
/// 書式は libsvn 共通の "&lt;scheme://host:port&gt; AuthName" (既定ポートでもポートを明示)。
/// </summary>
public static class RealmProbe
{
    public static async Task<ProbeResult> ProbeAsync(string url)
    {
        var uri = new Uri(url);
        return uri.Scheme switch
        {
            "http" or "https" => await ProbeHttpAsync(uri),
            "svn" => await ProbeSvnAsync(uri),
            "svn+ssh" => new ProbeResult(null, L.T("svn+ssh は ssh 側で認証するため対象外です", "svn+ssh authenticates through ssh; not handled")),
            _ => new ProbeResult(null, L.T("未対応のスキームです: ", "unsupported scheme: ") + uri.Scheme),
        };
    }

    /// <summary>.NET は svn:// の既定ポートを知らない (-1 になる) ので補う</summary>
    public static int Port(Uri uri) => uri.Port >= 0 ? uri.Port : uri.Scheme == "svn" ? 3690 : uri.Port;

    public static string Prefix(Uri uri) => $"<{uri.Scheme}://{uri.Host}:{Port(uri)}>";

    // ---- http / https ----

    static async Task<ProbeResult> ProbeHttpAsync(Uri uri)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseDefaultCredentials = false,
            // 認証情報は送らないので、自己署名証明書のサーバでも realm だけは調べられるようにする
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SVN/1.14 svnop");

        // 読み取りは匿名可で書き込みだけ認証のサーバもあるので、OPTIONS → PROPFIND の順に試す
        string? negotiateOnly = null;
        foreach (var method in new[] { HttpMethod.Options, new HttpMethod("PROPFIND") })
        {
            using var req = new HttpRequestMessage(method, uri);
            if (method.Method == "PROPFIND")
            {
                req.Headers.Add("Depth", "0");
                req.Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><propfind xmlns=\"DAV:\"><prop><resourcetype/></prop></propfind>",
                    Encoding.UTF8, "text/xml");
            }
            using var res = await http.SendAsync(req);
            if ((int)res.StatusCode is >= 300 and < 400)
                return new ProbeResult(null, L.T("リダイレクトされました", "redirected") + $" ({(int)res.StatusCode}): {res.Headers.Location}");
            if (res.StatusCode != HttpStatusCode.Unauthorized)
                continue;

            var schemes = new List<string>();
            foreach (var h in res.Headers.WwwAuthenticate)
            {
                schemes.Add(h.Scheme);
                if (h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
                    h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
                {
                    var realm = ParseRealm(h.Parameter);
                    if (realm != null)
                        return new ProbeResult($"{Prefix(uri)} {realm}", L.T($"{h.Scheme} 認証 ({method.Method} で検出)", $"{h.Scheme} authentication (found with {method.Method})"));
                }
            }
            negotiateOnly = string.Join(", ", schemes);
        }
        if (negotiateOnly != null)
            return new ProbeResult(null, L.T($"パスワード認証ではありません ({negotiateOnly})。Windows の資格情報で認証されるため保存は不要です", $"not password authentication ({negotiateOnly}); Windows credentials are used, nothing to store"));
        return new ProbeResult(null, L.T("認証を要求されませんでした (匿名でアクセスできる可能性があります)", "no authentication requested (anonymous access may be allowed)"));
    }

    /// <summary>
    /// realm="..." を取り出す。svn (serf) は \" の \ を外さずに realm 文字列にするので、
    /// 閉じ引用符の判定だけエスケープを考慮し、文字はそのまま残す。
    /// </summary>
    static string? ParseRealm(string? parameter)
    {
        if (parameter == null)
            return null;
        var i = parameter.IndexOf("realm=", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;
        i += 6;
        if (i < parameter.Length && parameter[i] == '"')
        {
            var sb = new StringBuilder();
            for (i++; i < parameter.Length && parameter[i] != '"'; i++)
            {
                if (parameter[i] == '\\' && i + 1 < parameter.Length)
                    sb.Append(parameter[i++]);
                sb.Append(parameter[i]);
            }
            return sb.ToString();
        }
        var end = parameter.IndexOf(',', i);
        return (end < 0 ? parameter[i..] : parameter[i..end]).Trim();
    }

    // ---- svn:// (ra_svn プロトコル) ----

    static async Task<ProbeResult> ProbeSvnAsync(Uri uri)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await tcp.ConnectAsync(uri.Host, Port(uri), cts.Token);
        tcp.ReceiveTimeout = 15000;
        using var stream = tcp.GetStream();
        var reader = new RaSvnReader(stream);

        // greeting: ( success ( minver maxver ( mechs ) ( caps ) ) )
        var greeting = reader.ReadItem() as List<object>;
        if (greeting is not ["success", ..])
            return new ProbeResult(null, L.T("svnserve の応答が想定外です", "unexpected response from svnserve"));

        // クライアントの応答: ( version ( caps ) url ra-client ( ) )
        var urlBytes = Encoding.UTF8.GetBytes(uri.AbsoluteUri);
        const string client = "svnop";
        var hello = Encoding.UTF8.GetBytes(
            $"( 2 ( edit-pipeline svndiff1 absent-entries depth mergeinfo log-revprops ) {urlBytes.Length}:");
        stream.Write(hello);
        stream.Write(urlBytes);
        stream.Write(Encoding.UTF8.GetBytes($" {client.Length}:{client} ( ) ) "));

        // auth request: ( success ( ( mechs... ) realm ) )
        var auth = reader.ReadItem() as List<object>;
        if (auth is ["success", List<object> body] && body is [List<object> mechs, byte[] realm, ..])
        {
            var names = mechs.OfType<string>().ToList();
            var realmString = $"{Prefix(uri)} {Encoding.UTF8.GetString(realm)}";
            if (names.Count == 0)
                return new ProbeResult(null, L.T("認証を要求されませんでした", "no authentication requested"));
            if (names.All(n => n is "ANONYMOUS" or "EXTERNAL"))
                return new ProbeResult(realmString, L.T("匿名アクセス可 (書き込み時に認証される場合あり: ", "anonymous access allowed (authentication may be required for writes: ") + string.Join(", ", names) + ")");
            return new ProbeResult(realmString, L.T("認証方式: ", "mechanisms: ") + string.Join(", ", names));
        }
        if (auth is ["failure", ..])
            return new ProbeResult(null, L.T("svnserve がエラーを返しました (URL のリポジトリが存在しない可能性があります)", "svnserve returned an error (the repository in the URL may not exist)"));
        return new ProbeResult(null, L.T("svnserve の応答が想定外です", "unexpected response from svnserve"));
    }

    /// <summary>ra_svn のデータ項目 (word / number / "len:bytes" / リスト) を読む最小実装</summary>
    sealed class RaSvnReader(Stream s)
    {
        int _peek = -1;

        int Next()
        {
            if (_peek >= 0) { var c = _peek; _peek = -1; return c; }
            var b = s.ReadByte();
            return b < 0 ? throw new SvnOpException(L.T("svnserve との接続が切れました", "connection to svnserve was closed")) : b;
        }

        int SkipSpace()
        {
            int c;
            while ((c = Next()) is ' ' or '\n' or '\r' or '\t') { }
            return c;
        }

        public object ReadItem()
        {
            var c = SkipSpace();
            if (c == '(')
            {
                var list = new List<object>();
                while (true)
                {
                    var d = SkipSpace();
                    if (d == ')')
                        return list;
                    _peek = d;
                    list.Add(ReadItem());
                }
            }
            if (char.IsAsciiDigit((char)c))
            {
                long n = c - '0';
                while (char.IsAsciiDigit((char)(c = Next())))
                    n = n * 10 + (c - '0');
                if (c != ':')
                {
                    _peek = c;
                    return n;
                }
                var buf = new byte[n];
                for (var i = 0; i < n; i++)
                    buf[i] = (byte)Next();
                return buf;
            }
            var sb = new StringBuilder();
            sb.Append((char)c);
            while (true)
            {
                var d = Next();
                if (char.IsAsciiLetterOrDigit((char)d) || d == '-')
                    sb.Append((char)d);
                else { _peek = d; break; }
            }
            return sb.ToString();
        }
    }
}
