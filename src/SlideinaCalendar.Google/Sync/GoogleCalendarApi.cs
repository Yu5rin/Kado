using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlideinaCalendar.Google.Sync;

/// <summary>一覧の1ページ。</summary>
/// <param name="Items">並んでいた項目。</param>
/// <param name="NextPageToken">続きがあるときの合図。無ければ null。</param>
/// <param name="NextSyncToken">次回の差分に使う印。最後のページにだけ付く。</param>
public sealed record GooglePage(
    IReadOnlyList<JsonElement> Items, string? NextPageToken, string? NextSyncToken);

/// <summary>アクセストークンを出す口。</summary>
public interface IAccessTokenSource
{
    /// <summary>いま使えるアクセストークンを返す。期限が近ければ取り直す。</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Google Calendar API の呼び出し。
/// <para>
/// <b>書き戻しは <c>patch</c> で行う。</b><c>update</c>（PUT）だと本文に無い項目が
/// 消える。ゲスト・通知・会議室など、このアプリに欄が無いものまで巻き添えになる。
/// </para>
/// <para>
/// 一覧は <c>syncToken</c> で差分だけを取る。<b>410 が返ったら token を捨てて
/// 全部取り直す</b>（要件書 6.3）。Google は古い token を無期限には覚えていない。
/// </para>
/// </summary>
public sealed class GoogleCalendarApi(HttpClient http, IAccessTokenSource tokens)
{
    private const string Root = "https://www.googleapis.com/calendar/v3";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IAccessTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>
    /// イベントの一覧を1ページ取る。
    /// <para>
    /// <paramref name="syncToken"/> を渡すと差分だけが返る。そのとき
    /// <c>showDeleted</c> は Google 側が自動で有効になり、消えたものが
    /// <c>status=cancelled</c> で降ってくる。
    /// </para>
    /// </summary>
    public async Task<GooglePage> ListEventsAsync(
        string calendarId,
        string? syncToken = null,
        string? pageToken = null,
        DateTimeOffset? from = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder($"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events");
        query.Append("?maxResults=250&singleEvents=false");

        if (syncToken is { Length: > 0 })
        {
            // 差分のときは timeMin を付けられない。付けると 400 になる
            query.Append("&syncToken=").Append(Uri.EscapeDataString(syncToken));
        }
        else
        {
            // 初回は取りすぎないよう期間を切る。全履歴は要らない
            query.Append("&showDeleted=false");
            if (from is { } start)
            {
                query.Append("&timeMin=").Append(Uri.EscapeDataString(start.ToString("O")));
            }
        }

        if (pageToken is { Length: > 0 })
        {
            query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        return ReadPage(await GetAsync(query.ToString(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>カレンダーの一覧を取る。名前と色を取り込むために使う。</summary>
    public async Task<GooglePage> ListCalendarsAsync(
        string? pageToken = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Root}/users/me/calendarList?maxResults=250";
        if (pageToken is { Length: > 0 }) url += "&pageToken=" + Uri.EscapeDataString(pageToken);

        return ReadPage(await GetAsync(url, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>イベントを作る。</summary>
    public async Task<JsonElement> InsertEventAsync(
        string calendarId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Post,
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを書き換える。
    /// <para><c>patch</c> なので、本文に入れなかった項目はそのまま残る。</para>
    /// </summary>
    public async Task<JsonElement> PatchEventAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Patch,
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを消す。
    /// <para>すでに無ければ成功として扱う。消したいのだから、無いのは望む状態。</para>
    /// </summary>
    public async Task DeleteEventAsync(
        string calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}");

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;

        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------

    private static GooglePage ReadPage(JsonElement root)
    {
        var items = new List<JsonElement>();
        if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(array.EnumerateArray());
        }

        return new GooglePage(
            items,
            Mapping.GoogleJson.Text(root, "nextPageToken"),
            Mapping.GoogleJson.Text(root, "nextSyncToken"));
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method, string url, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 削除などは本文が空で返る
        if (string.IsNullOrWhiteSpace(text)) return default;

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>断られていたら、見分けられる形にして投げる。</summary>
    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new GoogleApiException(response.StatusCode, ReadReason(body), body);
    }

    /// <summary>エラーの本文から理由を取り出す。読めなければ状態だけで判断する。</summary>
    private static string ReadReason(string body)
    {
        try
        {
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error");

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
