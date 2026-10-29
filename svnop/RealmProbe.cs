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
    /// <summary>realm 文字列の長さの上限 (これより長い応答は不正とみなす)</summary>
    const int MaxRealmLength = 1024;
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    /// <param name="insecure">https の証明書を検証しない (自己署名証明書のサーバ用。偽の realm を返されるおそれがある)</param>
    public static async Task<ProbeResult> ProbeAsync(string url, bool insecure = false)
    {
        var uri = new Uri(url);
        return uri.Scheme switch
        {
            "http" or "https" => await ProbeHttpAsync(uri, insecure),
            "svn" => await ProbeSvnAsync(uri),
            "svn+ssh" => new ProbeResult(null, L.T("svn+ssh は ssh 側で認証するため対象外です", "svn+ssh authenticates through ssh; not handled")),
            _ => new ProbeResult(null, L.T("未対応のスキームです: ", "unsupported scheme: ") + uri.Scheme),
        };
    }

    /// <summary>.NET は svn:// の既定ポートを知らない (-1 になる) ので補う</summary>
    public static int Port(Uri uri) => uri.Port >= 0 ? uri.Port : uri.Scheme == "svn" ? 3690 : uri.Port;

    public static string Prefix(Uri uri) => $"<{uri.Scheme}://{uri.Host}:{Port(uri)}>";

    // ---- http / https ----

    static async Task<ProbeResult> ProbeHttpAsync(Uri uri, bool insecure)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseDefaultCredentials = false,
        };
        // 既定では証明書を検証する (偽のサーバに偽の realm を返されないように)。
        // 自己署名証明書のサーバは --insecure を明示したときだけ受け入れる (認証情報は送らない)
        if (insecure)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = Deadline };
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
            HttpResponseMessage res;
            try
            {
                // 必要なのはヘッダだけなので本文は読まない
                res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new SvnOpException(L.T("サーバの証明書を検証できません。自己署名証明書のサーバなら --insecure を付けてください",
                                             "cannot verify the server certificate. For a server with a self-signed certificate, add --insecure"));
            }
            using var _ = res;
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
                    if (realm is { Length: > MaxRealmLength })
                        throw new SvnOpException(L.T("realm が長すぎます", "the realm is too long"));
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
        var deadline = DateTime.UtcNow + Deadline;  // 接続から解析完了までの期限
        using var cts = new CancellationTokenSource(Deadline);
        await tcp.ConnectAsync(uri.Host, Port(uri), cts.Token);
        tcp.ReceiveTimeout = tcp.SendTimeout = (int)Deadline.TotalMilliseconds;
        using var stream = tcp.GetStream();
        // 読み込みだけバッファする (BufferedStream は読み書きを混ぜられない)
        var reader = new RaSvnReader(new BufferedStream(stream), deadline);

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
            if (realm.Length > MaxRealmLength)
                throw new SvnOpException(L.T("realm が長すぎます", "the realm is too long"));
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

    /// <summary>
    /// ra_svn のデータ項目 (word / number / "len:bytes" / リスト) を読む最小実装。
    /// 悪意のあるサーバで止まったり落ちたりしないよう、総受信量・文字列長・要素数・深さ・期限に上限を設ける
    /// </summary>
    sealed class RaSvnReader(Stream s, DateTime deadline)
    {
        const int MaxTotalBytes = 64 * 1024;
        const int MaxStringBytes = 8 * 1024;
        const int MaxWordLength = 64;
        const int MaxListItems = 256;
        const int MaxDepth = 16;

        int _peek = -1;
        int _total;

        static SvnOpException Bad() => new(L.T("svnserve の応答が不正です", "invalid response from svnserve"));

        int Next()
        {
            if (_peek >= 0) { var c = _peek; _peek = -1; return c; }
            if (DateTime.UtcNow > deadline)
                throw new SvnOpException(L.T("svnserve の応答が時間内に終わりませんでした", "svnserve did not respond in time"));
            if (++_total > MaxTotalBytes)
                throw Bad();
            var b = s.ReadByte();
            return b < 0 ? throw new SvnOpException(L.T("svnserve との接続が切れました", "connection to svnserve was closed")) : b;
        }

        int SkipSpace()
        {
            int c;
            while ((c = Next()) is ' ' or '\n' or '\r' or '\t') { }
            return c;
        }

        public object ReadItem(int depth = 0)
        {
            if (depth > MaxDepth)
                throw Bad();
            var c = SkipSpace();
            if (c == '(')
            {
                var list = new List<object>();
                while (true)
                {
                    var d = SkipSpace();
                    if (d == ')')
                        return list;
                    if (list.Count >= MaxListItems)
                        throw Bad();
                    _peek = d;
                    list.Add(ReadItem(depth + 1));
                }
            }
            if (char.IsAsciiDigit((char)c))
            {
                long n = c - '0';
                while (char.IsAsciiDigit((char)(c = Next())))
                {
                    n = n * 10 + (c - '0');
                    if (n > int.MaxValue)
                        throw Bad();
                }
                if (c != ':')
                {
                    _peek = c;
                    return n;
                }
                if (n > MaxStringBytes)
                    throw Bad();
                var buf = new byte[n];
                for (var i = 0; i < n; i++)
                    buf[i] = (byte)Next();
                return buf;
            }
            if (!char.IsAsciiLetter((char)c))
                throw Bad();
            var sb = new StringBuilder();
            sb.Append((char)c);
            while (true)
            {
                var d = Next();
                if (char.IsAsciiLetterOrDigit((char)d) || d == '-')
                {
                    if (sb.Length >= MaxWordLength)
                        throw Bad();
                    sb.Append((char)d);
                }
                else { _peek = d; break; }
            }
            return sb.ToString();
        }
    }
}
