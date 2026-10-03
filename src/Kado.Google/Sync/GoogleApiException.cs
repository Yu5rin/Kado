using System.Net;
using Kado.Core.Net;

namespace Kado.Google.Sync;

/// <summary>
/// Google の API が断ってきた。
/// <para>
/// 呼び出し側が見分ける必要があるのは3つ。<b>410</b>（syncToken が古い＝全部取り直す）、
/// <b>403/429</b>（呼びすぎ＝待って出し直す）、<b>404</b>（相手にもう無い＝消えたとみなす）。
/// </para>
/// </summary>
public sealed class GoogleApiException(
    HttpStatusCode status, string reason, string? body = null,
    string? responseInfo = null, TimeSpan? retryAfter = null)
    : Exception($"Google の API が {(int)status} を返しました（{reason}）。"), IResponseDescribed
{
    /// <summary>
    /// 応答の要点（状態・Content-Type・Via・Retry-After・プロキシの認証方式）。記録に残す用。
    /// <para>
    /// 407 や、プロキシが返した HTML は、本文を見ても Google の言葉ではない。何が返ったのかを
    /// 1行で残しておくと、会社の回線で起きていることが追える。本文そのものは記録しない。
    /// </para>
    /// </summary>
    public string? ResponseInfo { get; } = responseInfo;

    /// <summary>Google が「この時間は待って」と伝えてきた長さ（<c>Retry-After</c>）。無ければ null。</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>応答の状態。</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>Google が付けてきた理由。</summary>
    public string Reason { get; } = reason;

    /// <summary>応答の本文。記録に残す用。</summary>
    public string? Body { get; } = body;

    /// <summary>
    /// Google が書いてきた説明。
    /// <para>
    /// <c>badRequest</c> のような理由だけでは何が悪いのか分からない。「時間の範囲が
    /// 空です」のように、相手の言い分をそのまま画面に出せるようにしておく。
    /// </para>
    /// </summary>
    public string? Description
    {
        get
        {
            if (Body is not { Length: > 0 } body) return null;

            try
            {
                var message = System.Text.Json.JsonDocument.Parse(body)
                    .RootElement.GetProperty("error")
                    .TryGetProperty("message", out var value) ? value.GetString() : null;

                // 理由と同じ文字列しか無いなら、二度言っても仕方がない
                return string.Equals(message, Reason, StringComparison.Ordinal) ? null : message;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                                           or KeyNotFoundException
                                           or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// syncToken が古くなった。
    /// <para>差分では追いつけないので、token を捨てて全部取り直す（要件書 6.3）。</para>
    /// </summary>
    public bool NeedsFullResync => Status == HttpStatusCode.Gone;

    /// <summary>呼びすぎ。少し待って出し直す。</summary>
    public bool IsRateLimited =>
        Status == HttpStatusCode.TooManyRequests ||
        (Status == HttpStatusCode.Forbidden &&
         Reason.Contains("imit", StringComparison.Ordinal));

    /// <summary>
    /// プロキシが認証を求めている（407）。Google の言葉ではなく、途中の中継が返したもの。
    /// </summary>
    public bool IsProxyAuthRequired => Status == HttpStatusCode.ProxyAuthenticationRequired;

    /// <summary>アクセストークンが通らなかった（401）。取り直せば直ることがある。</summary>
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    /// <summary>相手にもう無い。消えたものとして扱う。</summary>
    public bool IsMissing => Status == HttpStatusCode.NotFound;

    /// <summary>
    /// 権限が無くて断られた（読み取り専用になった、主催者でない、など）。出し直しても通らない。
    /// <para>呼びすぎ・容量の上限による 403（<see cref="IsRateLimited"/>）は含めない。</para>
    /// </summary>
    public bool IsPermissionDenied =>
        Status == HttpStatusCode.Forbidden &&
        !IsRateLimited &&
        !Reason.Contains("uota", StringComparison.Ordinal);

    /// <summary>出し直せば直る見込みがあるか。</summary>
    public bool IsTransient =>
        IsRateLimited ||
        Status is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
