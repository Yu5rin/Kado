using System.Net;

namespace Kado.Google.Sync;

/// <summary>
/// Google の API が断ってきた。
/// <para>
/// 呼び出し側が見分ける必要があるのは3つ。<b>410</b>（syncToken が古い＝全部取り直す）、
/// <b>403/429</b>（呼びすぎ＝待って出し直す）、<b>404</b>（相手にもう無い＝消えたとみなす）。
/// </para>
/// </summary>
public sealed class GoogleApiException(HttpStatusCode status, string reason, string? body = null)
    : Exception($"Google の API が {(int)status} を返しました（{reason}）。")
{
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

    /// <summary>相手にもう無い。消えたものとして扱う。</summary>
    public bool IsMissing => Status == HttpStatusCode.NotFound;

    /// <summary>出し直せば直る見込みがあるか。</summary>
    public bool IsTransient =>
        IsRateLimited ||
        Status is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
