using System.Net;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 送れないタスクは、1回だけ知らせて、毎回は言わない（予定は <see cref="EventPushRejectionTests"/>）。
/// Kado だけが持つ項目（URL・添付・繰り返し）は、そもそも送らないので警告も出ない。
/// </summary>
public class TaskPushRejectionTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTaskGateway _remote;

    public TaskPushRejectionTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TaskSyncEngine Engine =>
        new(Tasks, new TombstoneRepository(_connection), new SettingsRepository(_connection), _remote, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static GoogleApiException Invalid() =>
        new(HttpStatusCode.BadRequest, "invalid", """{"error":{"message":"Invalid value for: due"}}""");

    public void Dispose() => _connection.Dispose();

    private async Task SeedRejectedAsync()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "@default");

        Tasks.Upsert(Assert.Single(Tasks.All()) with { Title = "集計（変更）" });
        _remote.ThrowOnEveryWrite = Invalid();
    }

    [Fact]
    public async Task 断られたタスクには印を付け警告は一回だけ出す()
    {
        await SeedRejectedAsync();

        var first = await Engine.SyncAsync("@default", "@default");

        var warning = Assert.Single(first.Warnings);
        Assert.Equal(
            "Google に送れませんでした（集計（変更））：invalid — Invalid value for: due。手元にだけ残しています。直すと送り直します",
            warning);
        Assert.Contains("集計（変更）", Assert.Single(first.LogLines), StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(Tasks.All()).PushRejection);

        var attempts = _remote.WriteAttempts;
        for (var i = 0; i < 3; i++)
        {
            Assert.Empty((await Engine.SyncAsync("@default", "@default")).Warnings);
        }

        Assert.Equal(attempts, _remote.WriteAttempts);
    }

    [Fact]
    public async Task 直すともう一度送り送れたら印を外す()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("@default", "@default");

        _remote.ThrowOnEveryWrite = null;
        Tasks.Upsert(Assert.Single(Tasks.All()) with { Title = "集計（直した）" });

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.UpdatedRemote);
        Assert.Null(Assert.Single(Tasks.All()).PushRejection);
    }

    [Fact]
    public async Task Googleから新しい姿を受け取ると印を外す()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("@default", "@default");

        _remote.ThrowOnEveryWrite = null;
        _remote.Edit("g1", "Google 側で直した");

        await Engine.SyncAsync("@default", "@default");

        var after = Assert.Single(Tasks.All());
        Assert.Null(after.PushRejection);
        Assert.Equal("Google 側で直した", after.Title);
    }

    [Fact]
    public async Task 一時的な失敗には印を付けない()
    {
        await SeedRejectedAsync();
        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.True(report.Deferred);
        Assert.Null(Assert.Single(Tasks.All()).PushRejection);
    }

    [Fact]
    public async Task 不正な期限のタスクは送らず一回だけ知らせる()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "壊れた期限", TaskListId = "@default", Due = default(DateOnly),
        });

        var first = await Engine.SyncAsync("@default", "@default");
        var second = await Engine.SyncAsync("@default", "@default");

        Assert.Equal(0, _remote.WriteAttempts);
        Assert.Single(first.Warnings);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public async Task URLと添付と繰り返しを変えても送らず警告も出ない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "@default");

        // 送ろうとすれば断られる状態にしておく
        _remote.ThrowOnEveryWrite = Invalid();
        var attempts = _remote.WriteAttempts;

        Tasks.Upsert(Assert.Single(Tasks.All()) with
        {
            Url = "https://example.com/spec",
            Attachments = """[{"path":"C:\\資料\\図面.pdf"}]""",
            Repeat = "FREQ=WEEKLY;BYDAY=MO",
        });

        var report = await Engine.SyncAsync("@default", "@default");

        Assert.Empty(report.Warnings);
        Assert.Equal(attempts, _remote.WriteAttempts);
        Assert.Null(Assert.Single(Tasks.All()).PushRejection);
    }
}
