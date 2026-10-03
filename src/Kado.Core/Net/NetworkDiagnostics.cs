using System.Net;
using System.Net.Http;
using System.Text;

namespace Kado.Core.Net;

/// <summary>
/// 通信の失敗や応答を、shell.log に1行で残せる形に整える。更新・Google・実働日の配信で共通。
/// <para>
/// 失敗の理由が記録されていないと、会社のネットワークでだけ起きる不具合は追えない。
/// 通信・ファイルに触れない整形だけをここへ切り出して、テストで固定する。
/// </para>
/// <para>
/// <b>トークンや個人情報は書かない。</b>URL はクエリ（署名付きの転送先には
/// <c>?sig=…</c> のような一時的な鍵が付く）と資格情報を落とし、ホストと道筋だけを残す。
/// 応答ヘッダも、原因の切り分けに要るもの（状態・種類・大きさ・経由・残り回数・認証方式）だけを
/// 名指しで拾う。
/// </para>
/// </summary>
public static class NetworkDiagnostics
{
    /// <summary>例外の入れ子をたどる上限。自分自身を内側に持つような循環で回り続けないための歯止め。</summary>
    public const int MaxExceptionDepth = 8;

    /// <summary>
    /// 例外を「型名: メッセージ」の連鎖として1行にまとめる。内側の例外は <c> ← </c> でつなぐ。
    /// <para>
    /// ネットワークの失敗は、本当の理由が <c>InnerException</c> に入る。
    /// <code>
    /// HttpRequestException「送信できませんでした」
    ///   └ SocketException「接続が拒否されました」          ← プロキシ／ファイアウォール
    /// HttpRequestException「SSL 接続を確立できませんでした」
    ///   └ AuthenticationException「リモート証明書が無効」  ← 会社の通信検査
    /// </code>
    /// 外側だけでは分からないので、すべて出す。状態コードを持っていれば添える
    /// （403＝拒否・上限、407＝プロキシ認証）。
    /// </para>
    /// </summary>
    public static string Summarize(Exception? ex)
    {
        if (ex is null) return "(例外なし)";

        var sb = new StringBuilder();
        Append(sb, ex, 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Exception ex, int depth)
    {
        if (depth >= MaxExceptionDepth)
        {
            sb.Append(" ← (これ以上は省略)");
            return;
        }

        if (depth > 0) sb.Append(" ← ");

        sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        if (ex is HttpRequestException { StatusCode: { } code })
        {
            sb.Append(" [HTTP ").Append((int)code).Append(' ').Append(code).Append(']');
        }

        // 応答を持っていた例外（Google の API が断ってきた、など）は、状態・種類・経由の要点も添える
        if (ex is IResponseDescribed { ResponseInfo: { Length: > 0 } info })
        {
            sb.Append(" [").Append(info).Append(']');
        }

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) Append(sb, inner, depth + 1);
            return;
        }

        if (ex.InnerException is { } next) Append(sb, next, depth + 1);
    }

    /// <summary>
    /// ログに書く URL。<c>https://host/path</c> だけにする（クエリ・フラグメント・資格情報を落とす）。
    /// </summary>
    public static string SafeUrl(Uri? uri)
    {
        if (uri is null) return "(不明)";
        if (!uri.IsAbsoluteUri) return "(相対URL)";

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath}";
    }

    /// <summary><see cref="SafeUrl(Uri?)"/> の文字列版。URL として読めなければ空の印を返す。</summary>
    public static string SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? SafeUrl(uri) : "(URLではない)";

    /// <summary>
    /// 応答の要点を1行にする。
    /// <list type="bullet">
    /// <item>状態コード … 403（拒否・上限）と 407（プロキシ認証）の区別</item>
    /// <item>Content-Type … <c>text/html</c> なら、中身は目的のものではなく中継のエラーページ</item>
    /// <item>Via … 途中に中継が入っているか</item>
    /// <item>X-RateLimit-Remaining … GitHub の上限による 403 か、経路での遮断かの見分け</item>
    /// <item>Proxy-Authenticate … 407 のときの認証方式（Negotiate/NTLM/Basic など。方式名だけ）</item>
    /// </list>
    /// </summary>
    public static string DescribeResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var sb = new StringBuilder();

        sb.Append("応答 ").Append((int)response.StatusCode).Append(' ').Append(response.StatusCode);
        sb.Append(", Content-Type=").Append(response.Content.Headers.ContentType?.ToString() ?? "(なし)");
        sb.Append(", Content-Length=").Append(response.Content.Headers.ContentLength?.ToString() ?? "(なし)");
        sb.Append(", Via=").Append(Header(response, "Via"));

        if (response.Headers.Contains("X-RateLimit-Remaining"))
        {
            sb.Append(", X-RateLimit-Remaining=").Append(Header(response, "X-RateLimit-Remaining"));
        }

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            sb.Append(", Retry-After=").Append(
                retryAfter.Delta is { } delta ? $"{(int)delta.TotalSeconds}秒" : "(日時指定)");
        }

        if (response.Headers.ProxyAuthenticate.Count > 0)
        {
            sb.Append(", Proxy-Authenticate=")
                .Append(string.Join("/", response.Headers.ProxyAuthenticate.Select(a => a.Scheme)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 要求した URL と最終の URL が違うとき（リダイレクトされたとき）だけ、転送先を返す。
    /// 同じなら null。
    /// </summary>
    public static string? DescribeRedirect(string requestedUrl, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var finalUrl = SafeUrl(response.RequestMessage?.RequestUri);

        return string.Equals(finalUrl, SafeUrl(requestedUrl), StringComparison.Ordinal)
            ? null
            : $"転送先={finalUrl}";
    }

    /// <summary>
    /// 通信がどの経路を通るかを1行にする。
    /// <para>
    /// 会社の回線は、Windows の設定や PAC ファイルでプロキシ経由になっていることが多い。
    /// 通っているかどうかが分かるだけで、切り分けの幅がかなり狭まる。
    /// ホストとポートだけを出す。
    /// </para>
    /// </summary>
    /// <param name="proxy">調べる経路。</param>
    /// <param name="target">宛先。</param>
    /// <param name="usesDefaultCredentials">
    /// ハンドラが <c>DefaultProxyCredentials</c>（ログオン中のユーザー）を渡す設定か。
    /// <see cref="HttpClient.DefaultProxy"/> 自体は資格情報を持たないので、これを伝えないと
    /// 「資格情報=なし」と出て、実際の設定と食い違う（<see cref="KadoHttp"/>）。
    /// </param>
    public static string DescribeProxy(IWebProxy proxy, Uri target, bool usesDefaultCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(target);

        var via = proxy.GetProxy(target);

        if (via is null || proxy.IsBypassed(target))
        {
            return $"プロキシを経由しない（宛先 {target.Host}）";
        }

        var credentials = proxy.Credentials is not null
            ? "あり"
            : usesDefaultCredentials ? "ログオン中のユーザー" : "なし";

        return $"プロキシを経由する（{via.Host}:{via.Port}, 宛先 {target.Host}, 資格情報={credentials}）";
    }

    /// <summary>
    /// 中身が Windows の実行ファイルの書き出し（<c>MZ</c>）か。
    /// <para>
    /// ハッシュを確かめられない経路（API を使わずに組み立てた URL）では、途中の中継が
    /// 200 で返したエラーページを exe として置いてしまいかねない。置く前に、先頭の2バイトで
    /// 実行ファイルの形をしているかだけを確かめる。
    /// </para>
    /// </summary>
    public static bool LooksLikeExecutable(ReadOnlySpan<byte> head) =>
        head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';

    /// <summary>Content-Type が Web ページ（HTML）か。</summary>
    public static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "(なし)";
}

/// <summary>
/// 応答の要点（状態・Content-Type・経由など）を持っている例外。
/// <see cref="NetworkDiagnostics.Summarize"/> が、例外の連鎖に添えて記録に残す。
/// </summary>
public interface IResponseDescribed
{
    /// <summary>1行の要点。<see cref="NetworkDiagnostics.DescribeResponse"/> の出力。</summary>
    string? ResponseInfo { get; }
}
