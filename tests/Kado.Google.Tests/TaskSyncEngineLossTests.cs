using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// タスクが二重になる・消える筋。予定側（<see cref="EventSyncEngineLossTests"/>）と同じ考え方。
/// </summary>
public class TaskSyncEngineLossTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeTaskGateway _remote;

    public TaskSyncEngineLossTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TaskSyncEngine Engine =>
        new(Tasks, new TombstoneRepository(_connection), new SettingsRepository(_connection), _remote, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------
    // 404 で黙って作り直さない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 送ろうとして404ならGoogleに無い印を付けて作り直さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（変更）" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        var after = Assert.Single(Tasks.All());
        Assert.Equal("g1", after.GoogleTaskId);
        Assert.True(after.GoogleMissing);
        Assert.Equal(0, report.Relinked);
        Assert.Empty(_remote.Items);
        Assert.Contains(report.Warnings, w => w.Contains("集計（変更）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 印が付いたタスクは以後送らない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（変更）" });
        _remote.Items.Remove("g1");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（さらに変更）" });
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Empty(_remote.Items);
        Assert.Equal(0, report.CreatedRemote);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 移す指示が404でも作り直さず印を付ける()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { TaskListId = "list-b" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("list-b", "list-b");

        var after = Assert.Single(Tasks.All());
        Assert.True(after.GoogleMissing);
        Assert.Equal("g1", after.GoogleTaskId);
        Assert.Empty(_remote.Items);
        Assert.Contains(report.Warnings, w => w.Contains("集計", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 別のリストで見つかったら印を外して結び直す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（変更）" });
        _remote.Items.Remove("g1");
        await Engine.SyncAsync("list-a", "list-a");
        Assert.True(Tasks.All().Single().GoogleMissing);

        _remote.Add("g1", "集計（Bにいた）", due: "2026-09-24");
        await Engine.SyncAsync("list-b", "list-b");

        var after = Assert.Single(Tasks.All());
        Assert.False(after.GoogleMissing);
        Assert.Equal("list-b", after.GoogleTaskListId);
        Assert.Equal("list-b", after.TaskListId);
    }

    // ------------------------------------------------------------------
    // 未送信の編集が、Google 側の削除で警告なく消えない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 未送信の編集があるタスクを相手が消したら警告を出す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Tasks.All().Single() with { Title = "集計（まだ送っていない変更）" });
        _remote.Delete("g1");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Tasks.All());
        Assert.Contains(report.Warnings, w =>
            w.Contains("Google 側で削除されたため、こちらの未送信の変更を捨てました", StringComparison.Ordinal) &&
            w.Contains("集計（まだ送っていない変更）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 未送信の編集が無ければ相手が消しても警告を出さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        _remote.Delete("g1");
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 移し先に確かにあれば元からのdeletedで消さず入れ先を直す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        // tasks.move は Google で通ったのに、応答を受け取る前に切れた
        Tasks.Upsert(Tasks.All().Single() with { TaskListId = "list-b", Title = "集計（未送信の変更）" });
        _remote.Delete("g1");
        _remote.Located[("list-b", "g1")] = new JsonObject
        {
            ["id"] = "g1", ["title"] = "集計", ["status"] = "needsAction",
            ["updated"] = "2026-09-19T05:00:00.000Z", ["due"] = "2026-09-24T00:00:00.000Z",
        };

        var report = await Engine.SyncAsync("list-a", "list-a");

        Assert.Equal(0, report.DeletedLocal);
        var after = Assert.Single(Tasks.All());
        Assert.Equal("list-b", after.GoogleTaskListId);
        Assert.Equal("集計（未送信の変更）", after.Title);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 移し先にも無ければ消して未送信の編集は警告する()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { TaskListId = "list-b", Title = "集計（未送信の変更）" });
        _remote.Delete("g1");

        var report = await Engine.SyncAsync("list-a", "list-a");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Tasks.All());
        Assert.Contains(report.Warnings, w => w.Contains("集計（未送信の変更）", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 結び付いていない同じものを探す範囲
    // ------------------------------------------------------------------

    [Fact]
    public async Task このアプリだけのリストの同じ題と期限のタスクは引き受けない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:private", Note = "私用のメモ",
        });
        _remote.Add("g1", "集計", due: "2026-09-24");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.CreatedLocal);
        Assert.Equal(2, Tasks.All().Count);

        var untouched = Tasks.Find("t1")!;
        Assert.Null(untouched.GoogleTaskId);
        Assert.Equal("local:private", untouched.TaskListId);
        Assert.Equal("私用のメモ", untouched.Note);
    }

    [Fact]
    public async Task 別のリストの未送信の新規も引き受けない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "list-b",
        });
        _remote.Add("g1", "集計", due: "2026-09-24");

        await Engine.SyncAsync("list-a", "list-a");

        Assert.Equal(2, Tasks.All().Count);
        Assert.Null(Tasks.Find("t1")!.GoogleTaskId);
    }
}
