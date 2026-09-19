using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlideinaCalendar.Google.Sync;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// Google の代わり。持っているイベントをそのまま返す。
/// <para>通信を挟まずに、同期の順序と判断だけを試せるようにする。</para>
/// </summary>
internal sealed class FakeEventGateway : IEventGateway
{
    private int _nextId = 1;
    private int _clock;

    /// <summary>項目ごとの版。印と突き合わせて、変わったものだけ返す。</summary>
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

    /// <summary>印を出した時点の版。</summary>
    private readonly Dictionary<string, int> _tokens = new(StringComparer.Ordinal);

    /// <summary>相手が持っているイベント。Google の ID で引く。</summary>
    public Dictionary<string, JsonObject> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>次の一覧で返す差分の印。</summary>
    public string? NextSyncToken { get; set; } = "token-1";

    /// <summary>一覧のときに受け取った印。差分で呼ばれたかを見る。</summary>
    public List<string?> SeenSyncTokens { get; } = [];

    /// <summary>消された Google の ID。</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>一度だけ 410 を返す。syncToken が古くなった場面を作る。</summary>
    public bool FailNextWithGone { get; set; }

    /// <summary>次の書き込みでこの例外を投げる。</summary>
    public GoogleApiException? ThrowOnWrite { get; set; }

    public Task<GooglePage> ListAsync(
        string calendarId, string? syncToken, string? pageToken, CancellationToken cancellationToken)
    {
        SeenSyncTokens.Add(syncToken);

        if (FailNextWithGone)
        {
            FailNextWithGone = false;
            throw new GoogleApiException(HttpStatusCode.Gone, "fullSyncRequired");
        }

        // 印を渡されたら、その時点より後に変わったものだけ返す。本物と同じ挙動にしないと、
        // 変わっていないものを毎回上書きしてしまう作りを見逃す
        var since = syncToken is { Length: > 0 } && _tokens.TryGetValue(syncToken, out var watermark)
            ? watermark
            : -1;

        var items = Items
            .Where(pair => Version(pair.Key) > since)
            .Select(pair => JsonDocument.Parse(pair.Value.ToJsonString()).RootElement.Clone())
            .ToArray();

        if (NextSyncToken is { Length: > 0 }) _tokens[NextSyncToken] = _clock;

        return Task.FromResult(new GooglePage(items, null, NextSyncToken));
    }

    public Task<JsonElement> InsertAsync(string calendarId, JsonObject body, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        var id = $"g{_nextId++}";
        var stored = body.DeepClone().AsObject();
        stored["id"] = id;
        stored["updated"] = "2026-09-19T00:00:00.000Z";
        Items[id] = stored;
        Touch(id);

        return Task.FromResult(Parse(stored));
    }

    public Task<JsonElement> PatchAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        if (!Items.TryGetValue(eventId, out var stored))
        {
            throw new GoogleApiException(HttpStatusCode.NotFound, "notFound");
        }

        // patch なので、本文にある項目だけを当てる
        foreach (var pair in body) stored[pair.Key] = pair.Value?.DeepClone();
        stored["updated"] = "2026-09-19T01:00:00.000Z";
        Touch(eventId);

        return Task.FromResult(Parse(stored));
    }

    public Task DeleteAsync(string calendarId, string eventId, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        Deleted.Add(eventId);
        Items.Remove(eventId);
        _versions.Remove(eventId);

        return Task.CompletedTask;
    }

    /// <summary>相手に終日予定を1件置く。</summary>
    public string Add(string id, string summary, string date, string? endDate = null)
    {
        Items[id] = new JsonObject
        {
            ["id"] = id,
            ["summary"] = summary,
            ["status"] = "confirmed",
            ["updated"] = "2026-09-19T00:00:00.000Z",
            ["start"] = new JsonObject { ["date"] = date },
            ["end"] = new JsonObject { ["date"] = endDate ?? DateOnly.Parse(date).AddDays(1).ToString("yyyy-MM-dd") },
        };

        Touch(id);
        return id;
    }

    /// <summary>相手が取り消した状態にする。</summary>
    public void Cancel(string id)
    {
        Items[id] = new JsonObject { ["id"] = id, ["status"] = "cancelled" };
        Touch(id);
    }

    /// <summary>相手側で書き換わったことにする。</summary>
    public void Edit(string id, string summary)
    {
        Items[id]["summary"] = summary;
        Touch(id);
    }

    private void Touch(string id) => _versions[id] = ++_clock;

    private int Version(string id) => _versions.TryGetValue(id, out var value) ? value : 0;

    private static JsonElement Parse(JsonObject value) =>
        JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
}
