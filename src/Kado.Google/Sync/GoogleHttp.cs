using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kado.Core.Net;

namespace Kado.Google.Sync;

/// <summary>
/// Google の API へ送る共通の道具。カレンダー・タスク・ドライブの3つが同じ送り方をする。
/// <para>
/// 送るのは「要求を作る関数」を受け取って、出し直すたびに新しく作る
/// （<see cref="HttpRequestMessage"/> は一度送ると使い回せない）。
/// </para>
/// <list type="bullet">
/// <item><b>401</b> … トークンを<b>一度だけ</b>取り直して出し直す。取り直しても同じなら諦める。</item>
/// <item><b>429・5xx・呼びすぎの 403</b> … <see cref="GoogleRetryPolicy"/> に従って待ち、数回だけ出し直す。</item>
/// </list>
/// </summary>
internal static class GoogleHttp
{
    /// <summary>
    /// 送る。成功でなくても、最後の応答を返す（呼び出し側が <see cref="EnsureOkAsync"/> で例外にする）。
    /// </summary>
    /// <param name="http">通信に使うクライアント。</param>
    /// <param name="tokens">アクセストークンの出どころ。</param>
    /// <param name="createRequest">要求を作る。出し直すたびに呼ばれる。</param>
    /// <param name="retry">出し直しの方針。</param>
    /// <param name="timeout">1回の送信ぶんの上限。null ならクライアントの <see cref="HttpClient.Timeout"/> に任せる。</param>
    /// <param name="cancellationToken">中止。</param>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, IAccessTokenSource tokens, Func<HttpRequestMessage> createRequest,
        GoogleRetryPolicy retry, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var retries = 0;
        var tokenRefreshed = false;

        while (true)
        {
            var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } span) limit.CancelAfter(span);

            var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return response;

            // 401: トークンを一度だけ取り直す。取り直しても同じトークンなら、出し直しても同じ結果になる
            if (response.StatusCode == HttpStatusCode.Unauthorized && !tokenRefreshed)
            {
                tokenRefreshed = true;

                var fresh = await tokens.RefreshAccessTokenAsync(token, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(fresh, token, StringComparison.Ordinal))
                {
                    response.Dispose();
                    continue;
                }

                return response;
            }

            var rateLimited403 = response.StatusCode == HttpStatusCode.Forbidden &&
                                 await IsRateLimitedAsync(response, cancellationToken).ConfigureAwait(false);

            var wait = retry.NextDelay(
                request.Method, response.StatusCode, rateLimited403, RetryAfterOf(response), retries);

            if (wait is not { } delay) return response;

            response.Dispose();
            retries++;

            await retry.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>応答の <c>Retry-After</c>（秒、または日時）を待つ長さにする。無ければ null。</summary>
    public static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta) return delta;

        if (header?.Date is { } date)
        {
            var span = date - (now ?? DateTimeOffset.UtcNow);
            return span > TimeSpan.Zero ? span : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>403 の本文を見て、呼びすぎ・容量の上限だったか。</summary>
    private static async Task<bool> IsRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new GoogleApiException(response.StatusCode, ReadReason(body), body).IsRateLimited;
    }

    /// <summary>断られていたら、見分けられる形にして投げる。</summary>
    public static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new GoogleApiException(
            response.StatusCode, ReadReason(body), body,
            NetworkDiagnostics.DescribeResponse(response), RetryAfterOf(response));
    }

    /// <summary>
    /// 応答の本文を JSON として読む。削除などは本文が空で返るので、そのときは <c>default</c>。
    /// <para>JSON でない本文（プロキシの HTML など）は <see cref="JsonException"/> になる。呼び出し側が文言にする。</para>
    /// </summary>
    public static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text)) return default;

        // 読み終えた文書は返す（Clone した要素は文書と切り離されている）
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>エラーの本文から理由を取り出す。読めなければ状態だけで判断する。</summary>
    public static string ReadReason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");

            if (error.TryGetProperty("errors", out var list) &&
                list.ValueKind == JsonValueKind.Array &&
                list.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first &&
                Mapping.GoogleJson.Text(first, "reason") is { } reason)
            {
                return reason;
            }

            return Mapping.GoogleJson.Text(error, "message") ?? "理由なし";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "理由なし";
        }
    }
}
