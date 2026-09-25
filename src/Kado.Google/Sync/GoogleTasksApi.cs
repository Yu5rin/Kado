using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kado.Google.Sync;

/// <summary>
/// Google Tasks API の呼び出し。
/// <para>
/// <b>Calendar と違って <c>syncToken</c> が無い。</b>差分は <c>updatedMin</c>（この時刻より
/// 後に変わったもの）で取る。<c>showDeleted</c> と <c>showHidden</c> を立てないと、
/// 消えたものと完了したものが降ってこず、こちらに残り続ける。
/// </para>
/// <para>
/// <c>updatedMin</c> は時計のずれに弱い。こちらの時計が進んでいると、その間の変更を
/// 取りこぼす。少し前から取り直すことで埋める（<see cref="Overlap"/>）。
/// </para>
/// </summary>
public sealed class GoogleTasksApi(HttpClient http, IAccessTokenSource tokens)
{
    private const string Root = "https://tasks.googleapis.com/tasks/v1";

    /// <summary>
    /// 差分を取るときに、どれだけ前から取り直すか。
    /// <para>時計のずれで取りこぼすより、同じものを二度読むほうが安い。</para>
    /// </summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IAccessTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>タスクリストの一覧を取る。</summary>
    public async Task<GooglePage> ListTaskListsAsync(
        string? pageToken = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Root}/users/@me/lists?maxResults=100";
        if (pageToken is { Length: > 0 }) url += "&pageToken=" + Uri.EscapeDataString(pageToken);

        return ReadPage(await GetAsync(url, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// タスクの一覧を1ページ取る。
    /// <para>
    /// <paramref name="updatedSince"/> を渡すと、その時刻より後に変わったものだけが返る。
    /// 時計のずれを見越して <see cref="Overlap"/> ぶん手前から取る。
    /// </para>
    /// </summary>
    public async Task<GooglePage> ListTasksAsync(
        string taskListId,
        DateTimeOffset? updatedSince = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder($"{Root}/lists/{Uri.EscapeDataString(taskListId)}/tasks");

        // 立てないと、消えたものと完了したものが降ってこない
        query.Append("?maxResults=100&showDeleted=true&showHidden=true&showCompleted=true");

        if (updatedSince is { } since)
        {
            query.Append("&updatedMin=").Append(Uri.EscapeDataString((since - Overlap).ToString("O")));
        }

        if (pageToken is { Length: > 0 })
        {
            query.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        return ReadPage(await GetAsync(query.ToString(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>タスクを作る。</summary>
    public async Task<JsonElement> InsertTaskAsync(
        string taskListId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Post,
            $"{Root}/lists/{Uri.EscapeDataString(taskListId)}/tasks",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>タスクを書き換える。<c>patch</c> なので、入れなかった項目は残る。</summary>
    public async Task<JsonElement> PatchTaskAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return await SendAsync(
            HttpMethod.Patch,
            $"{Root}/lists/{Uri.EscapeDataString(taskListId)}/tasks/{Uri.EscapeDataString(taskId)}",
            body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// タスクを別のリストへ移す。
    /// <para>消して作り直すのではなく <c>move</c> を使う。並び順や親子関係を保てる。</para>
    /// </summary>
    public async Task<JsonElement> MoveTaskAsync(
        string taskListId, string taskId, string destinationTaskListId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTaskListId);

        var url = $"{Root}/lists/{Uri.EscapeDataString(taskListId)}/tasks/" +
                  $"{Uri.EscapeDataString(taskId)}/move?destinationTasklist=" +
                  Uri.EscapeDataString(destinationTaskListId);

        return await SendAsync(HttpMethod.Post, url, new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>タスクを消す。すでに無ければ成功として扱う。</summary>
    public async Task DeleteTaskAsync(
        string taskListId, string taskId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{Root}/lists/{Uri.EscapeDataString(taskListId)}/tasks/{Uri.EscapeDataString(taskId)}");

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

        // Tasks は syncToken を返さない
        return new GooglePage(items, Mapping.GoogleJson.Text(root, "nextPageToken"), null);
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

        if (string.IsNullOrWhiteSpace(text)) return default;

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new GoogleApiException(response.StatusCode, ReadReason(body), body);
    }

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
