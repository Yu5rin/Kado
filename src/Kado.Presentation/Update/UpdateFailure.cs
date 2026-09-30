using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Xml;

namespace Kado.Presentation.Update;

/// <summary>更新の通信が失敗した理由の種類。利用者に伝える文言を分けるためのもの。</summary>
public enum UpdateFailureKind
{
    /// <summary>上に当てはまらない。</summary>
    Other,

    /// <summary>
    /// 403（や 429）。GitHub の API の未認証の上限（1 時間 60 回、同じ出口の IP で共有）に
    /// 当たったときに出る。
    /// </summary>
    RateLimited,

    /// <summary>407。プロキシが認証を求めている。</summary>
    ProxyAuthRequired,

    /// <summary>プロキシ経由の接続（CONNECT）を確立できなかった。407 以外。</summary>
    ProxyError,

    /// <summary>時間内に応答が無かった。</summary>
    Timeout,

    /// <summary>接続できない（名前が引けない、拒否された、届かない）。</summary>
    CannotConnect,

    /// <summary>TLS の証明書を確かめられなかった。社内の通信検査が入っているときに出る。</summary>
    CertificateProblem,

    /// <summary>404。</summary>
    NotFound,

    /// <summary>5xx。</summary>
    ServerError,

    /// <summary>応答を読み取れなかった（壊れた XML/JSON、想定外の形）。</summary>
    UnreadableResponse,

    /// <summary>取りに行ったのに、Web ページ（HTML）などが返った。中継のエラーページなど。</summary>
    WebPageInsteadOfFile,
}

/// <summary>
/// 取りに行ったものではなく、別のもの（Web ページ、実行ファイルの形をしていないもの）が返った。
/// <para>
/// 会社のプロキシは、止めた通信の代わりに 200 でエラーページを返すことがある。
/// 落としたものをそのまま実行するので、これを exe として扱ってはいけない。
/// </para>
/// </summary>
public sealed class UnexpectedContentException(string contentType)
    : Exception($"取りに行ったものではないものが返りました（Content-Type={contentType}）。")
{
    public string ContentType { get; } = contentType;
}

/// <summary>更新を確かめられなかった理由。画面に出す文言とリリースのページを持つ。</summary>
/// <param name="Kind">理由の種類。</param>
/// <param name="Message">利用者に見せる文言。例外の型名は含まない。</param>
/// <param name="ReleasePageUrl">手で確かめるときのリリースのページ。組み立てられなければ空。</param>
public sealed record UpdateFailure(UpdateFailureKind Kind, string Message, string ReleasePageUrl)
{
    /// <summary>「更新を確かめられませんでした。」に理由を添えた文言にする。</summary>
    public static UpdateFailure From(Exception ex, string releasePageUrl)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return new UpdateFailure(Classify(ex), CheckMessage(ex), releasePageUrl);
    }

    /// <summary>確認が失敗したときの文言。</summary>
    public static string CheckMessage(Exception ex) =>
        "更新を確かめられませんでした。" + Reason(ex);

    /// <summary>ダウンロードや入れ替えが失敗したときの文言。手で差し替える道も添える。</summary>
    public static string DownloadMessage(Exception ex)
    {
        var reason = Reason(ex);

        return reason.Contains("リリースページ", StringComparison.Ordinal)
            ? "更新できませんでした。" + reason
            : "更新できませんでした。" + reason + "リリースのページから手で差し替えてください。";
    }

    /// <summary>
    /// 例外から、画面に出してよい理由の文を作る。<b>型名は出さない。</b>
    /// <para>
    /// Kado 自身が日本語で書いたメッセージ（ハッシュが合わない、など）は、そのまま通す。
    /// .NET が返す英語のメッセージはパスや状態コードを含むことがあり、利用者には手掛かりに
    /// ならないので、種類ごとの言い換えにする。詳細は shell.log に残る。
    /// </para>
    /// </summary>
    public static string Reason(Exception ex)
    {
        var kind = Classify(ex);

        if (kind == UpdateFailureKind.Other && ContainsKana(ex.Message)) return ex.Message;

        return Reason(kind);
    }

    /// <summary>種類ごとの、利用者向けの理由の文。</summary>
    public static string Reason(UpdateFailureKind kind) => kind switch
    {
        UpdateFailureKind.RateLimited =>
            "配布元への問い合わせが回数の上限に達していました。同じネットワークを使う人たちで共有される上限なので、" +
            "自分が何度も押していなくても起こります。しばらく時間をおくか、リリースページから直接ご確認ください。",
        UpdateFailureKind.ProxyAuthRequired =>
            "会社などのプロキシサーバーが認証を求めていて、通信を通せませんでした（407）。" +
            "ネットワークの管理者にご確認いただくか、リリースページから直接ダウンロードしてください。",
        UpdateFailureKind.ProxyError =>
            "プロキシサーバーを経由した接続を確立できませんでした。" +
            "ネットワークの管理者にご確認いただくか、リリースページから直接ダウンロードしてください。",
        UpdateFailureKind.Timeout =>
            "配布元から時間内に応答がありませんでした。ネットワークの状態を確認して、しばらくしてからもう一度お試しください。",
        UpdateFailureKind.CannotConnect =>
            "配布元（GitHub）へ接続できませんでした。インターネットに繋がっているか、" +
            "社内のネットワークで GitHub への通信が許可されているかをご確認ください。",
        UpdateFailureKind.CertificateProblem =>
            "安全な接続（証明書）を確認できませんでした。社内のセキュリティ機器が通信を検査している場合に起こることがあります。" +
            "ネットワークの管理者にご確認ください。",
        UpdateFailureKind.NotFound =>
            "配布元でリリースが見つかりませんでした。リリースのページをご確認ください。",
        UpdateFailureKind.ServerError =>
            "配布元が一時的に応答できない状態でした。しばらくしてからもう一度お試しください。",
        UpdateFailureKind.UnreadableResponse =>
            "配布元からの応答を読み取れませんでした。ネットワークの途中で応答が差し替えられている可能性があります。",
        UpdateFailureKind.WebPageInsteadOfFile =>
            "取りに行ったものではなく、別のもの（Web ページなど）が返りました。ネットワークの途中で" +
            "差し替えられている可能性があります（プロキシのエラーページなど）。" +
            "リリースページから直接ダウンロードしてください。",
        _ => "ネットワークをご確認ください。",
    };

    /// <summary>「通信を確かめる」の一覧に出す短い言い方。</summary>
    public static string ShortReason(UpdateFailureKind kind) => kind switch
    {
        UpdateFailureKind.RateLimited => "問い合わせ回数の上限",
        UpdateFailureKind.ProxyAuthRequired => "プロキシの認証が必要",
        UpdateFailureKind.ProxyError => "プロキシ経由の接続に失敗",
        UpdateFailureKind.Timeout => "時間内に応答なし",
        UpdateFailureKind.CannotConnect => "接続できない",
        UpdateFailureKind.CertificateProblem => "証明書を確認できない",
        UpdateFailureKind.NotFound => "見つからない",
        UpdateFailureKind.ServerError => "配布元が応答できない",
        UpdateFailureKind.UnreadableResponse => "応答を読み取れない",
        UpdateFailureKind.WebPageInsteadOfFile => "Web ページが返った",
        _ => "原因は shell.log を参照",
    };

    /// <summary>
    /// 例外の連鎖（内側の例外まで）から、失敗の種類を決める。
    /// <para>
    /// ネットワークの失敗は、本当の理由が内側の例外に入る。外側の <c>HttpRequestException</c>
    /// だけでは「通信できません」としか分からない。
    /// </para>
    /// </summary>
    public static UpdateFailureKind Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var chain = Flatten(ex).ToList();

        // 状態コードがあれば、それがいちばん確かな手掛かり
        foreach (var http in chain.OfType<HttpRequestException>())
        {
            if (http.StatusCode is { } code && ClassifyStatus(code) is { } byStatus) return byStatus;
        }

        if (chain.OfType<UnexpectedContentException>().Any()) return UpdateFailureKind.WebPageInsteadOfFile;

        if (chain.OfType<HttpRequestException>().Any(h => h.HttpRequestError == HttpRequestError.ProxyTunnelError))
        {
            return UpdateFailureKind.ProxyError;
        }

        if (chain.OfType<AuthenticationException>().Any() ||
            chain.OfType<HttpRequestException>().Any(h => h.HttpRequestError == HttpRequestError.SecureConnectionError))
        {
            return UpdateFailureKind.CertificateProblem;
        }

        if (chain.OfType<TimeoutException>().Any() || chain.OfType<OperationCanceledException>().Any())
        {
            return UpdateFailureKind.Timeout;
        }

        if (chain.OfType<System.Net.Sockets.SocketException>().Any() ||
            chain.OfType<HttpRequestException>().Any(h =>
                h.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError))
        {
            return UpdateFailureKind.CannotConnect;
        }

        if (chain.Any(e => e is JsonException or XmlException or InvalidDataException))
        {
            return UpdateFailureKind.UnreadableResponse;
        }

        return UpdateFailureKind.Other;
    }

    /// <summary>状態コードから種類を決める。決められないものは null。</summary>
    public static UpdateFailureKind? ClassifyStatus(HttpStatusCode status) => (int)status switch
    {
        403 or 429 => UpdateFailureKind.RateLimited,
        407 => UpdateFailureKind.ProxyAuthRequired,
        404 => UpdateFailureKind.NotFound,
        >= 500 and <= 599 => UpdateFailureKind.ServerError,
        _ => null,
    };

    /// <summary>例外と、その内側の例外をすべて並べる（<see cref="AggregateException"/> も展開する）。</summary>
    internal static IEnumerable<Exception> Flatten(Exception ex, int depth = 0)
    {
        if (depth >= UpdateDiagnostics.MaxExceptionDepth) yield break;

        yield return ex;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                foreach (var e in Flatten(inner, depth + 1)) yield return e;
            }

            yield break;
        }

        if (ex.InnerException is { } next)
        {
            foreach (var e in Flatten(next, depth + 1)) yield return e;
        }
    }

    private static bool ContainsKana(string text)
    {
        foreach (var c in text)
        {
            // ひらがな（U+3040-309F）とカタカナ（U+30A0-30FF）。Kado が日本語で書いたと分かる
            if (c is >= '぀' and <= 'ヿ') return true;
        }

        return false;
    }
}
