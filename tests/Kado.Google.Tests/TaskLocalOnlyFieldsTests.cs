using System.Text.Json;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// タスクの URL と添付（ファイルの場所）は Kado だけの項目。Google Tasks に欄が無いので、
/// <b>Google には一切送らず</b>、同期で Google から受け取っても消えない。
/// <para>
/// 消えると、使う人が付けた資料の場所が、Google 側で題を直されただけで黙って無くなる
/// （<c>TaskMapper.FromGoogle</c> は受け取るたびにタスクを作り直す）。
/// </para>
/// </summary>
public class TaskLocalOnlyFieldsTests : IDisposable
{
    private const string Url = "https://example.com/spec";
    private const string Attachments = """[{"path":"C:\\資料\\図面.pdf"},{"path":"\\\\server\\share\\議事録"}]""";

    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTaskGateway _remote;

    public TaskLocalOnlyFieldsTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TaskSyncEngine Engine => new(Tasks, new TombstoneRepository(_connection), new SettingsRepository(_connection), _remote, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    public void Dispose() => _connection.Dispose();

    /// <summary>Google に送る本文・Google から来る本文に、Kado だけの項目が紛れ込んでいないか。</summary>
    private static void AssertNoLocalOnlyKeys(System.Text.Json.Nodes.JsonObject remote)
    {
        foreach (var key in new[] { "url", "Url", "attachments", "Attachments", "links" })
        {
            Assert.False(remote.ContainsKey(key), $"Google に「{key}」が送られている");
        }
    }

    // ------------------------------------------------------------------
    // マッパー
    // ------------------------------------------------------------------

    [Fact]
    public void Googleから受け取っても既存のURLと添付を引き継ぐ()
    {
        var existing = new TaskItem { Id = "t1", Title = "集計", Url = Url, Attachments = Attachments };

        var value = TaskMapper.FromGoogle(
            Json("""{"id":"g1","title":"集計（Googleで変更）","due":"2026-09-24T00:00:00.000Z"}"""),
            "@default", existing: existing);

        Assert.Equal("集計（Googleで変更）", value.Title);
        Assert.Equal(Url, value.Url);
        Assert.Equal(Attachments, value.Attachments);
    }

    [Fact]
    public void 初めて受け取るタスクにはURLも添付も無い()
    {
        var value = TaskMapper.FromGoogle(Json("""{"id":"g1","title":"集計"}"""), "@default");

        Assert.Null(value.Url);
        Assert.Null(value.Attachments);
    }

    [Fact]
    public void Googleに送る本文にURLも添付も入らない()
    {
        var body = TaskMapper.ToGoogle(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), Url = Url, Attachments = Attachments,
        });

        AssertNoLocalOnlyKeys(body);

        // メモにも混ぜない。向こうで直したメモと食い違い、本文が汚れる
        Assert.Null(body["notes"]);
    }

    [Fact]
    public void URLや添付を変えただけでは送る対象にならない()
    {
        var received = TaskMapper.FromGoogle(
            Json("""{"id":"g1","title":"集計","status":"needsAction","due":"2026-09-24T00:00:00.000Z"}"""),
            "@default");

        Assert.False(TaskMapper.NeedsPush(received));
        Assert.False(TaskMapper.NeedsPush(received with { Url = Url, Attachments = Attachments }));
    }

    // ------------------------------------------------------------------
    // 同期（取り込み・書き戻し・新規送信・移動のどれでも消えない）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Google側の更新を取り込んでもURLと添付が残る()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Tasks.All().Single() with { Url = Url, Attachments = Attachments });

        _remote.Edit("g1", "集計（Googleで変更）");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("集計（Googleで変更）", stored.Title);
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task 書き戻した応答を取り込んでもURLと添付が残る()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        // こちらで題を直し、URL と添付も付ける。送られるのは題だけ
        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（こちらで変更）", Url = Url, Attachments = Attachments });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.UpdatedRemote);
        Assert.Equal("集計（こちらで変更）", _remote.Items["g1"]["title"]!.GetValue<string>());
        AssertNoLocalOnlyKeys(_remote.Items["g1"]);

        var stored = Assert.Single(Tasks.All());
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task 新規に送った応答を取り込んでもURLと添付が残る()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            Url = Url, Attachments = Attachments,
        });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.CreatedRemote);
        AssertNoLocalOnlyKeys(Assert.Single(_remote.Items).Value);

        var stored = Assert.Single(Tasks.All());
        Assert.NotNull(stored.GoogleTaskId);
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task 結び付いていない同じタスクを引き受けてもURLと添付が残る()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            Url = Url, Attachments = Attachments,
        });
        _remote.Add("g1", "集計", due: "2026-09-24");

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("g1", stored.GoogleTaskId);
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task リストを移しても内容を一緒に送ってもURLと添付が残る()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with
        {
            TaskListId = "list-b", Title = "集計（変更）", Url = Url, Attachments = Attachments,
        });

        var report = await Engine.SyncAsync("list-b", "list-b");

        Assert.Equal(1, report.Moved);
        Assert.Equal(1, report.UpdatedRemote);
        AssertNoLocalOnlyKeys(_remote.Items["g1"]);

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("list-b", stored.TaskListId);
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task 見つからない印が外れて別のリストで結び直されてもURLと添付が残る()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（変更）", Url = Url, Attachments = Attachments });
        _remote.Items.Remove("g1");
        await Engine.SyncAsync("list-a", "list-a");
        Assert.True(Tasks.All().Single().GoogleMissing);

        _remote.Add("g1", "集計（Bにいた）", due: "2026-09-24");
        await Engine.SyncAsync("list-b", "list-b");

        var stored = Assert.Single(Tasks.All());
        Assert.False(stored.GoogleMissing);
        Assert.Equal(Url, stored.Url);
        Assert.Equal(Attachments, stored.Attachments);
    }

    [Fact]
    public async Task URLや添付を変えただけでは何も送らない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");
        var writesBefore = _remote.WriteAttempts;

        Tasks.Upsert(Tasks.All().Single() with { Url = Url, Attachments = Attachments });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        // 送るものが無いのに Google の更新時刻だけ進めない（他の人の画面にも影響する）
        Assert.Equal(0, report.UpdatedRemote);
        Assert.Equal(writesBefore, _remote.WriteAttempts);
        AssertNoLocalOnlyKeys(_remote.Items["g1"]);
    }
}
