using System.Net;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// タスクの同期が止まる・食い違う筋。予定側（<see cref="EventSyncEngineRecoveryTests"/>）と同じ考え方。
/// </summary>
public class TaskSyncEngineRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeTaskGateway _remote;

    public TaskSyncEngineRecoveryTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private TaskSyncEngine Engine =>
        new(Tasks, Tombstones, new SettingsRepository(_connection), _remote, _clock);

    public void Dispose() => _connection.Dispose();

    private static GoogleApiException Api(HttpStatusCode status, string reason) => new(status, reason);

    private void RecordDeletion(string localId, string googleId) =>
        Tombstones.Record(localId, TombstoneRepository.TaskKind, googleId, DateTimeOffset.Now, "@default");

    // ------------------------------------------------------------------
    // 削除の送信で 4xx が出ても、そのリストの同期を止めない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 削除が400で断られても警告にして記録を残し続きを進める()
    {
        _remote.Add("g1", "消すタスク", due: "2026-09-24");
        _remote.Add("g2", "別のタスク", due: "2026-09-25");
        await Engine.SyncAsync("@default", "@default");

        Tasks.Upsert(Tasks.All().Single(t => t.GoogleTaskId == "g2") with { Title = "別のタスク（変更）" });

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.BadRequest, "invalid");

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Equal("別のタスク（変更）", (string?)_remote.Items["g2"]["title"]);
        Assert.Contains(report.Warnings, w => w.Contains("削除を伝えられませんでした", StringComparison.Ordinal));
        Assert.NotNull(Tombstones.Find("gone-local", TombstoneRepository.TaskKind));
    }

    [Fact]
    public async Task 削除が権限で403なら記録を捨てて警告する()
    {
        _remote.Add("g1", "消すタスク", due: "2026-09-24");
        await Engine.SyncAsync("@default", "@default");

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.Forbidden, "forbidden");

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Null(Tombstones.Find("gone-local", TombstoneRepository.TaskKind));
        Assert.Contains(report.Warnings, w => w.Contains("削除できません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 削除が410ならすでに消えているので記録を片付ける()
    {
        _remote.Add("g1", "消すタスク", due: "2026-09-24");
        await Engine.SyncAsync("@default", "@default");

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.Gone, "deleted");

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Null(Tombstones.Find("gone-local", TombstoneRepository.TaskKind));
        Assert.Empty(report.Warnings);
    }

    // ------------------------------------------------------------------
    // 全件を取り直すとき、Google で消えたタスクを拾う
    // ------------------------------------------------------------------

    [Fact]
    public async Task 前回の時刻を捨てた全件取得では消えたタスクを手元からも消す()
    {
        _remote.Add("g1", "残る", due: "2026-09-24");
        _remote.Add("g2", "消えた", due: "2026-09-25");
        await Engine.SyncAsync("@default", "@default");

        // 繋ぎ直しで同期の状態が空になった。その間に Google で消え、deleted でも降ってこない
        new SettingsRepository(_connection).ClearSyncState();
        _remote.Items.Remove("g2");

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Equal(["g1"], Tasks.All().Select(t => t.GoogleTaskId!).ToArray());
        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(_remote.Deleted);
    }

    [Fact]
    public async Task 全件取得で消えていたタスクに未送信の変更があれば残して印と警告を出す()
    {
        _remote.Add("g2", "消えた", due: "2026-09-25");
        await Engine.SyncAsync("@default", "@default");

        Tasks.Upsert(Tasks.All().Single() with { Title = "手元で直した" });
        new SettingsRepository(_connection).ClearSyncState();
        _remote.Items.Remove("g2");

        var report = await Engine.SyncAsync("@default", "@default");

        var kept = Assert.Single(Tasks.All());
        Assert.True(kept.GoogleMissing);
        Assert.Equal(0, report.DeletedLocal);
        Assert.Contains(report.Warnings, w => w.Contains("手元で直した", StringComparison.Ordinal));
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 差分の取り込みでは一覧に載らないタスクを消さない()
    {
        _remote.Add("g1", "変わっていない", due: "2026-09-24");
        await Engine.SyncAsync("@default", "@default");

        // 2回目は差分。何も変わっていないので一覧は空
        await Engine.SyncAsync("@default", "@default");

        Assert.Single(Tasks.All());
    }

    // ------------------------------------------------------------------
    // 親子のタスクは別のリストへ移させない
    // ------------------------------------------------------------------

    private async Task SeedParentAndChildAsync()
    {
        _remote.Add("p1", "親", due: "2026-09-24");
        _remote.Add("c1", "子", due: "2026-09-24");
        _remote.Items["c1"]["parent"] = "p1";
        await Engine.SyncAsync("list-a", "list-a");
    }

    [Fact]
    public async Task 子のタスクのリスト移動は送らず元のリストへ戻して内容を送る()
    {
        await SeedParentAndChildAsync();
        Tasks.Upsert(Tasks.All().Single(t => t.GoogleTaskId == "c1") with { TaskListId = "list-b", Title = "子（変更）" });

        var report = await Engine.SyncAsync("list-b", "list-b");

        Assert.Empty(_remote.Moved);
        var child = Tasks.All().Single(t => t.GoogleTaskId == "c1");
        Assert.Equal("list-a", child.TaskListId);
        Assert.Contains(report.Warnings, w => w.Contains("サブタスク", StringComparison.Ordinal));
        Assert.Equal("子（変更）", (string?)_remote.Items["c1"]["title"]);
    }

    [Fact]
    public async Task 子を持つ親のリスト移動も送らない()
    {
        await SeedParentAndChildAsync();
        Tasks.Upsert(Tasks.All().Single(t => t.GoogleTaskId == "p1") with { TaskListId = "list-b" });

        var report = await Engine.SyncAsync("list-b", "list-b");

        Assert.Empty(_remote.Moved);
        Assert.Equal("list-a", Tasks.All().Single(t => t.GoogleTaskId == "p1").TaskListId);
        Assert.Contains(report.Warnings, w => w.Contains("サブタスク", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 親子でないタスクは今までどおり移す()
    {
        _remote.Add("g1", "単独", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { TaskListId = "list-b" });
        var report = await Engine.SyncAsync("list-b", "list-b");

        Assert.Single(_remote.Moved);
        Assert.Equal(1, report.Moved);
    }

    [Fact]
    public async Task 移すのが断られても内容の変更は送り希望を元に戻す()
    {
        _remote.Add("g1", "単独", due: "2026-09-24");
        await Engine.SyncAsync("list-a", "list-a");

        Tasks.Upsert(Tasks.All().Single() with { TaskListId = "list-b", Title = "単独（変更）" });
        _remote.ThrowOnWrite = Api(HttpStatusCode.BadRequest, "invalid");

        var report = await Engine.SyncAsync("list-b", "list-b");

        Assert.Equal("単独（変更）", (string?)_remote.Items["g1"]["title"]);
        Assert.Equal("list-a", Tasks.All().Single().TaskListId);
        Assert.Contains(report.Warnings, w => w.Contains("移せませんでした", StringComparison.Ordinal));

        var again = await Engine.SyncAsync("list-a", "list-a");
        Assert.Empty(again.Warnings);
        Assert.Empty(_remote.Moved);
    }
}
