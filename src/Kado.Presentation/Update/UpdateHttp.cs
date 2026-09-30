using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Kado.Presentation.Update;

/// <summary>
/// 更新の通信の共通部品。
/// </summary>
public static class UpdateHttp
{
    /// <summary>リダイレクトを自分で辿るときの上限。</summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// リダイレクトを自分で辿り、行き先が変わるたびに許可された場所かを確かめる。
    /// <para>
    /// GitHub の Releases はアセットの実体を <c>githubusercontent.com</c> 側へ
    /// リダイレクトすることが多い。<c>AllowAutoRedirect</c> の既定はリダイレクト先を
    /// 検査しないので、応答の途中で差し替えられても気づけない。落としたものを
    /// そのまま実行するので、<see cref="ReleaseFeed.IsAllowedDownloadUrl"/> を毎回通す。
    /// </para>
    /// <para>
    /// 呼び出し側の <see cref="HttpClient"/> は <c>AllowAutoRedirect = false</c> であること。
    /// </para>
    /// </summary>
    /// <param name="http">リダイレクトを自動で辿らない設定のクライアント。</param>
    /// <param name="url">最初の URL。</param>
    /// <param name="accept">要求の Accept。応答の種類を要求ごとに伝える。</param>
    /// <param name="cancellationToken">中止・時間切れ。</param>
    /// <param name="log">転送のたびに1行残す。URL はクエリを落として書く。</param>
    public static async Task<HttpResponseMessage> FollowAllowedRedirectsAsync(
        HttpClient http, string url, string accept, CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ReleaseFeed.IsAllowedDownloadUrl(url))
            {
                throw new InvalidOperationException("更新の取得先が許されていない場所です。");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));

            var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            // 相対な Location もあり得るので、いまの URL を基準に組み立てる
            url = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(url), location).ToString();

            log?.Invoke($"転送 {(int)response.StatusCode} → {UpdateDiagnostics.SafeUrl(url)}");

            response.Dispose();
        }

        throw new InvalidOperationException("更新の取得でリダイレクトが多すぎました。");
    }

    /// <summary>状態コードがリダイレクトか。</summary>
    public static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// 応答の本文を、上限までしか受け取らずに読む。
    /// <para>大きさが分かっていて上限を超えるなら、読み始めずに断る。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">上限を超えた。</exception>
    public static async Task<byte[]> ReadLimitedAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new InvalidDataException($"応答が大きすぎます（{length}バイト、上限 {maxBytes}バイト）。");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"応答が大きすぎます（上限 {maxBytes}バイト）。");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>成功でなければ、状態コードを持った例外にする。</summary>
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode) return;

        throw new HttpRequestException(
            "成功以外の状態コードが返りました", null, response.StatusCode);
    }
}
