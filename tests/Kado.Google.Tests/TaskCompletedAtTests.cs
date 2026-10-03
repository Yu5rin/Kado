using System.Text.Json;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// タスクの完了日時が、同期した時刻に置き換わらないこと。
/// <para>
/// status だけ送ると Google が「いま」を完了日時に入れ、手元の完了日時も置き換わる。
/// 「N実働日 遅れて完了」の表示が、同期のたびに狂う。
/// </para>
/// </summary>
public class TaskCompletedAtTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static readonly DateTimeOffset Done = new(2026, 9, 10, 8, 30, 0, TimeSpan.Zero);

    private const string OpenRaw = """{"id":"t1","title":"集計","status":"needsAction"}""";

    private const string DoneRaw =
        """{"id":"t1","title":"集計","status":"completed","completed":"2026-09-10T08:30:00.000Z"}""";

    [Fact]
    public void 完了にしたときは手元の完了日時をcompletedとして送る()
    {
        var value = TaskMapper.FromGoogle(Json(OpenRaw), "@default") with { IsDone = true, CompletedAt = Done };

        var body = TaskMapper.ToGoogle(value);

        Assert.Equal("completed", (string?)body["status"]);
        Assert.Equal("2026-09-10T08:30:00.000Z", (string?)body["completed"]);
    }

    [Fact]
    public void 未完了へ戻したときはcompletedを消しhiddenも外す()
    {
        var value = TaskMapper.FromGoogle(Json(DoneRaw), "@default") with { IsDone = false, CompletedAt = null };

        var body = TaskMapper.ToGoogle(value);

        Assert.Equal("needsAction", (string?)body["status"]);
        Assert.True(body.ContainsKey("completed"));
        Assert.Null(body["completed"]);
        Assert.False((bool)body["hidden"]!);
    }

    [Fact]
    public void 完了のまま変えていなければcompletedを送らない()
    {
        var value = TaskMapper.FromGoogle(Json(DoneRaw), "@default") with { Title = "集計（改）" };

        var body = TaskMapper.ToGoogle(value);

        Assert.False(body.ContainsKey("completed"));
        Assert.False(body.ContainsKey("hidden"));
    }

    [Fact]
    public void 未完了のままなら完了に関わるキーを送らない()
    {
        var value = TaskMapper.FromGoogle(Json(OpenRaw), "@default") with { Title = "集計（改）" };

        var body = TaskMapper.ToGoogle(value);

        Assert.False(body.ContainsKey("completed"));
        Assert.False(body.ContainsKey("hidden"));
    }

    [Fact]
    public void 完了のまま作るタスクも完了日時を送る()
    {
        var body = TaskMapper.ToGoogle(new TaskItem { Id = "n1", Title = "済み", IsDone = true, CompletedAt = Done });

        Assert.Equal("2026-09-10T08:30:00.000Z", (string?)body["completed"]);
    }

    [Fact]
    public void 完了日時を持たない完了タスクは完了日時を送らない()
    {
        var body = TaskMapper.ToGoogle(new TaskItem { Id = "n1", Title = "古いデータ", IsDone = true });

        Assert.False(body.ContainsKey("completed"));
    }

    [Fact]
    public void 完了の状態が同じならNeedsPushはfalse()
    {
        var value = TaskMapper.FromGoogle(Json(DoneRaw), "@default");

        Assert.False(TaskMapper.NeedsPush(value));
    }

    [Fact]
    public void 完了を取り消して付け直し日時が変わったら送る()
    {
        var value = TaskMapper.FromGoogle(Json(DoneRaw), "@default") with { CompletedAt = Done.AddDays(2) };

        Assert.True(TaskMapper.NeedsPush(value));
        Assert.Equal("2026-09-12T08:30:00.000Z", (string?)TaskMapper.ToGoogle(value)["completed"]);
    }

    // ------------------------------------------------------------------
    // 同期を通しても、手元の完了日時が残る
    // ------------------------------------------------------------------

    [Fact]
    public async Task 完了にして同期しても手元の完了日時が同期した時刻に置き換わらない()
    {
        using var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var remote = new FakeTaskGateway(clock);
        var tasks = new TaskRepository(connection);
        var engine = new TaskSyncEngine(
            tasks, new TombstoneRepository(connection), new SettingsRepository(connection), remote, clock);

        remote.Add("g1", "集計", due: "2026-09-12");
        await engine.SyncAsync("@default", "local:mytasks");

        // 9/10 に完了にした（同期は9/19）
        tasks.Upsert(tasks.All().Single() with { IsDone = true, CompletedAt = Done });
        await engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("2026-09-10T08:30:00.000Z", (string?)remote.Items["g1"]["completed"]);
        Assert.Equal(Done, tasks.All().Single().CompletedAt);
    }

    [Fact]
    public async Task 未完了に戻して同期するとGoogleのcompletedが消えhiddenも外れる()
    {
        using var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var remote = new FakeTaskGateway(clock);
        var tasks = new TaskRepository(connection);
        var engine = new TaskSyncEngine(
            tasks, new TombstoneRepository(connection), new SettingsRepository(connection), remote, clock);

        remote.Add("g1", "集計", due: "2026-09-12", done: true);
        remote.Items["g1"]["completed"] = "2026-09-10T08:30:00.000Z";
        remote.Items["g1"]["hidden"] = true;
        await engine.SyncAsync("@default", "local:mytasks");
        Assert.True(tasks.All().Single().IsDone);

        tasks.Upsert(tasks.All().Single() with { IsDone = false, CompletedAt = null });
        await engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("needsAction", (string?)remote.Items["g1"]["status"]);
        Assert.Null(remote.Items["g1"]["completed"]);
        Assert.False((bool)remote.Items["g1"]["hidden"]!);
        Assert.False(tasks.All().Single().IsDone);
    }
}
