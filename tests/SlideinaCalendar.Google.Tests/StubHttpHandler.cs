using System.Net;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// 決まった応答を返す HTTP。
/// <para>認可サーバを立てずに、トークンの交換と取り直しを試すために使う。</para>
/// </summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond)
    : HttpMessageHandler
{
    /// <summary>受け取った要求の本文。何を送ったかを確かめる。</summary>
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var (status, body) = respond(request);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
