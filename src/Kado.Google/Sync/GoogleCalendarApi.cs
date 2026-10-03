using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kado.Google.Sync;

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

    /// <summary>
    /// Google が 401 で断ってきたときに、トークンを取り直す。
    /// <para>
    /// 期限は残っているはずなのに通らないことがある（時計のずれ、Google 側での失効）。
    /// <paramref name="rejectedToken"/> は断られたトークン。すでに別の呼び出しが取り直していれば、
    /// 取り直さずにその新しいものを返す。<b>取り直せない実装は、同じトークンを返せばよい</b>
    /// （呼び出し側は、前と同じなら出し直さない）。
    /// </para>
    /// </summary>
    Task<string> RefreshAccessTokenAsync(string rejectedToken, CancellationToken cancellationToken = default) =>
        GetAccessTokenAsync(cancellationToken);
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
public sealed class GoogleCalendarApi(
    HttpClient http, IAccessTokenSource tokens, GoogleRetryPolicy? retry = null)
{
    private const string Root = "https://www.googleapis.com/calendar/v3";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IAccessTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>429・5xx のときに待って出し直す方針。省略すると本物の待ち方（<see cref="GoogleRetryPolicy.Default"/>）。</summary>
    private readonly GoogleRetryPolicy _retry = retry ?? GoogleRetryPolicy.Default;

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

    /// <summary>
    /// カレンダーの一覧を取る。名前と色を取り込むために使う。
    /// <para>
    /// <b><c>showHidden=true</c> を付ける。</b>付けないと、Google の一覧から隠しただけの
    /// カレンダーが返ってこない。それを「消えた」と取り違えると、中の予定ごと手元から捨ててしまう。
    /// </para>
    /// </summary>
    public async Task<GooglePage> ListCalendarsAsync(
        string? pageToken = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Root}/users/me/calendarList?maxResults=250&showHidden=true";
        if (pageToken is { Length: > 0 }) url += "&pageToken=" + Uri.EscapeDataString(pageToken);

        return ReadPage(await GetAsync(url, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 色の一覧を取る。
    /// <para>番号と実際の色の対応。こちらで決め打ちせず、毎回ここから取る。</para>
    /// </summary>
    public async Task<Mapping.GoogleColors> GetColorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Mapping.GoogleColors.Read(
                await GetAsync($"{Root}/colors", cancellationToken).ConfigureAwait(false));
        }
        catch (GoogleApiException)
        {
            // 取れなくても同期は続けられる。色の書き戻しだけを諦める
            return Mapping.GoogleColors.Empty;
        }
    }

    /// <summary>
    /// 一覧側のカレンダーの設定を書き換える。
    /// <para>
    /// 名前の付け替え（<c>summaryOverride</c>）と表示色は、<b>その人だけの設定</b>として
    /// ここに持つ。カレンダーそのものの名前（<c>summary</c>）とは別で、共有している
    /// 相手には影響しない。
    /// </para>
    /// </summary>
    public async Task<JsonElement> PatchCalendarListAsync(
        string calendarId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Patch,
            $"{Root}/users/me/calendarList/{Uri.EscapeDataString(calendarId)}",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// カレンダーを作る。
    /// <para>
    /// 実働日データの入れ先（Kado）が Google 側に無いときに使う。要求している
    /// 権限のうち <c>calendar.app.created</c> が、このアプリが作ったカレンダーの
    /// 作成と管理を許している。他人のカレンダーには触れない。
    /// </para>
    /// <para>作ったものは自分の一覧にも載るので、次の同期で降りてくる。</para>
    /// </summary>
    /// <returns>作られたカレンダー。<c>id</c> を持つ。</returns>
    public async Task<JsonElement> InsertCalendarAsync(
        string summary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        return await SendAsync(
            HttpMethod.Post,
            $"{Root}/calendars",
            new JsonObject { ["summary"] = summary },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを作る。
    /// <para>
    /// <c>supportsAttachments=true</c> を常に付ける。添付を送らない本文でも無害なので、
    /// 添付を扱うかどうかで呼び分けずに済ませている。
    /// </para>
    /// </summary>
    public async Task<JsonElement> InsertEventAsync(
        string calendarId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Post,
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events?supportsAttachments=true",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを1件取る。
    /// <para>
    /// 消されたイベントは 404 ではなく <c>status=cancelled</c> で返ってくることがある。
    /// 「そこにあるか」を見るときは、呼び出し側で状態も見ること。
    /// </para>
    /// </summary>
    public async Task<JsonElement> GetEventAsync(
        string calendarId, string eventId, CancellationToken cancellationToken = default) =>
        await GetAsync(
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}",
            cancellationToken).ConfigureAwait(false);

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
            $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}" +
            "?supportsAttachments=true",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを別のカレンダーへ移す。
    /// <para>
    /// 消して作り直すと、ゲスト・会議 URL・添付・色など、このアプリが扱わない項目が
    /// 落ちる。<c>move</c> はそれらを保ったまま運ぶ。応答は移ったあとのイベントの姿。
    /// </para>
    /// <para>
    /// 繰り返しのうち1回だけを差し替えた回（<c>recurringEventId</c> を持つ子）には使えない
    /// （Google 側の制約）。呼び出し側で止めること。
    /// </para>
    /// </summary>
    public async Task<JsonElement> MoveEventAsync(
        string calendarId, string eventId, string destinationCalendarId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCalendarId);

        var url = $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/" +
                  $"{Uri.EscapeDataString(eventId)}/move?destination=" +
                  Uri.EscapeDataString(destinationCalendarId);

        return await SendAsync(HttpMethod.Post, url, new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// イベントを消す。
    /// <para>すでに無ければ成功として扱う。消したいのだから、無いのは望む状態。</para>
    /// </summary>
    public async Task DeleteEventAsync(
        string calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        var url = $"{Root}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";

        using var response = await SendRawAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken).ConfigureAwait(false);

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
        using var response = await SendRawAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);

        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method, string url, JsonObject body, CancellationToken cancellationToken)
    {
        // 出し直すときのために、本文は文字列で持っておき、要求のたびに作る
        var json = body.ToJsonString();

        using var response = await SendRawAsync(
            () => new HttpRequestMessage(method, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);

        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 送る。401 はトークンを一度だけ取り直し、429・5xx は待って数回だけ出し直す
    /// （<see cref="GoogleHttp.SendAsync"/>）。
    /// </summary>
    private Task<HttpResponseMessage> SendRawAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken) =>
        GoogleHttp.SendAsync(_http, _tokens, createRequest, _retry, timeout: null, cancellationToken);

    private static Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        GoogleHttp.ReadBodyAsync(response, cancellationToken);

    /// <summary>断られていたら、見分けられる形にして投げる。</summary>
    private static Task EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        GoogleHttp.EnsureOkAsync(response, cancellationToken);
}
