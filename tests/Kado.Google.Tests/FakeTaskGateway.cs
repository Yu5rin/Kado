using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>Google Tasks の代わり。</summary>
internal sealed class FakeTaskGateway(ManualClock clock) : ITaskGateway
{
    private int _nextId = 1;

    /// <summary>項目ごとの最終更新。updatedMin と突き合わせる。</summary>
    private readonly Dictionary<string, DateTimeOffset> _updated = new(StringComparer.Ordinal);

    /// <summary>いまの時刻。同期の側と同じ時計を使う。</summary>
    public DateTimeOffset Now => clock.GetUtcNow();

    public Dictionary<string, JsonObject> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>一覧のときに受け取った updatedMin。</summary>
    public List<DateTimeOffset?> SeenSince { get; } = [];

    public List<string> Deleted { get; } = [];

    public GoogleApiException? ThrowOnWrite { get; set; }

    public Task<GooglePage> ListAsync(
        string taskListId, DateTimeOffset? updatedSince, string? pageToken, CancellationToken cancellationToken)
    {
        SeenSince.Add(updatedSince);

        var items = Items
            .Where(pair => updatedSince is not { } since || Updated(pair.Key) > since)
            .Select(pair => Parse(pair.Value))
            .ToArray();

        return Task.FromResult(new GooglePage(items, null, null));
    }

    public Task<JsonElement> InsertAsync(string taskListId, JsonObject body, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        var id = $"t{_nextId++}";
        var stored = body.DeepClone().AsObject();
        stored["id"] = id;
        stored["updated"] = Now.ToString("O");
        Items[id] = stored;
        Touch(id);

        return Task.FromResult(Parse(stored));
    }

    public Task<JsonElement> PatchAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        if (!Items.TryGetValue(taskId, out var stored))
        {
            throw new GoogleApiException(HttpStatusCode.NotFound, "notFound");
        }

        foreach (var pair in body) stored[pair.Key] = pair.Value?.DeepClone();
        stored["updated"] = Now.ToString("O");
        Touch(taskId);

        return Task.FromResult(Parse(stored));
    }

    public Task DeleteAsync(string taskListId, string taskId, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is { } error) { ThrowOnWrite = null; throw error; }

        Deleted.Add(taskId);
        Items.Remove(taskId);
        _updated.Remove(taskId);

        return Task.CompletedTask;
    }

    /// <summary>相手にタスクを1件置く。</summary>
    public string Add(string id, string title, string? due = null, bool done = false)
    {
        Items[id] = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["status"] = done ? "completed" : "needsAction",
            ["updated"] = Now.ToString("O"),
            ["due"] = due is null ? null : $"{due}T00:00:00.000Z",
        };

        Touch(id);
        return id;
    }

    /// <summary>相手が消した状態にする。</summary>
    public void Delete(string id)
    {
        Items[id] = new JsonObject { ["id"] = id, ["deleted"] = true };
        Touch(id);
    }

    /// <summary>完了して一覧から隠れただけの状態にする。消したのとは別物。</summary>
    public void Hide(string id)
    {
        Items[id]["hidden"] = true;
        Items[id]["status"] = "completed";
        Touch(id);
    }

    /// <summary>相手側で書き換わったことにする。</summary>
    public void Edit(string id, string title)
    {
        Items[id]["title"] = title;
        Touch(id);
    }

    /// <summary>書き換わったことにする。時計を進めてから記録する。</summary>
    private void Touch(string id) => _updated[id] = clock.Advance(TimeSpan.FromMinutes(1));

    private DateTimeOffset Updated(string id) =>
        _updated.TryGetValue(id, out var value) ? value : DateTimeOffset.MinValue;

    private static JsonElement Parse(JsonObject value) =>
        JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
}
