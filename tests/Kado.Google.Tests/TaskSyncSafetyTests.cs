using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 点検で見つかった、タスクの同期で使う人のタスクを壊しうる不具合の見張り（予定は EventSyncSafetyTests）。
/// </summary>
public class TaskSyncSafetyTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeTaskGateway _remote;

    public TaskSyncSafetyTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private SettingsRepository Settings => new(_connection);

    private TaskSyncEngine Engine => new(Tasks, Tombstones, Settings, _remote, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task 容量の上限で断られたら印を付けずにその回の送信を打ち切る()
    {
        for (var i = 1; i <= 3; i++)
        {
            Tasks.Upsert(new TaskItem
            {
                Id = $"t{i}", Title = $"作業{i}", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            });
        }

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.Forbidden, "quotaExceeded");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.True(report.Deferred);
        Assert.Equal(1, _remote.WriteAttempts);
        Assert.All(Tasks.All(), t => Assert.Null(t.PushRejection));

        _remote.ThrowOnEveryWrite = null;
        var next = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(3, next.CreatedRemote);
    }

    [Fact]
    public async Task 新規を送っている間に保存された編集を応答で潰さない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Tasks.Upsert(Tasks.Find("t1")! with { Title = "集計（変更）" });
        };

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("集計（変更）", stored.Title);
        Assert.NotNull(stored.GoogleTaskId);

        var next = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, next.UpdatedRemote);
        Assert.Equal(0, next.CreatedRemote);
        Assert.Equal("集計（変更）", Assert.Single(_remote.Items).Value["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task 書き換えを送っている間に保存された編集を応答で潰さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Assert.Single(Tasks.All()) with { Title = "集計（送る分）" });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Tasks.Upsert(Tasks.Find("google:g1")! with { Title = "集計（送信中の編集）" });
        };

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("集計（送信中の編集）", Assert.Single(Tasks.All()).Title);

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("集計（送信中の編集）", Assert.Single(_remote.Items).Value["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task 送っている間に手元で消された新規は復活させず向こうの写しを消す()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Tasks.Delete("t1");
        };

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Empty(Tasks.All());

        var next = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, next.DeletedRemote);
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 持ち主の分からない削除記録は404なら別のリストのものとして残す()
    {
        Tombstones.Record("old", TombstoneRepository.TaskKind, "elsewhere", DateTimeOffset.Now);

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, Tombstones.Count());
    }

    [Fact]
    public async Task 持ち主が分かっている削除記録は404なら片付ける()
    {
        Tombstones.Record("old", TombstoneRepository.TaskKind, "elsewhere", DateTimeOffset.Now, "@default");

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, Tombstones.Count());
    }

    private async Task ImportThenAssignOtherListAsync()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        Tasks.Upsert(Assert.Single(Tasks.All()) with { TaskListId = "local:other" });

        _remote.Items.Remove("g1");
        Settings.SetSyncState(TaskSyncEngine.SinceKey("@default"), string.Empty);
    }

    [Fact]
    public async Task 全件の取り直しで移す指示を出した行は一覧に無くても消さない()
    {
        await ImportThenAssignOtherListAsync();

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, report.DeletedLocal);

        var kept = Assert.Single(Tasks.All());
        Assert.True(kept.GoogleMissing);
        Assert.Equal("local:other", kept.TaskListId);
    }

    [Fact]
    public async Task 全件の取り直しで移し先に確かにあれば移動は済んでいたとして場所を直す()
    {
        await ImportThenAssignOtherListAsync();

        _remote.Located[("local:other", "g1")] = new JsonObject
        {
            ["id"] = "g1", ["title"] = "集計", ["status"] = "needsAction",
            ["updated"] = "2026-09-19T12:00:00.000Z",
        };

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, report.DeletedLocal);

        var kept = Assert.Single(Tasks.All());
        Assert.False(kept.GoogleMissing);
        Assert.Equal("local:other", kept.GoogleTaskListId);
    }

    [Fact]
    public async Task メモが食い違う未送信のタスクは同じものとして引き受けない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            Note = "手元にだけあるメモ",
        });

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        _remote.Add("g1", "集計", due: "2026-09-24");
        _remote.Items["g1"]["notes"] = "向こうの別のメモ";

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(2, Tasks.All().Count);
        Assert.Equal("手元にだけあるメモ", Tasks.Find("t1")!.Note);
    }

    [Fact]
    public async Task 中身が揃っている未送信のタスクは送信の途中で切れたものとして引き受ける()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks", Note = "メモ",
        });

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        _remote.Add("g1", "集計", due: "2026-09-24");
        _remote.Items["g1"]["notes"] = "メモ";

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("t1", stored.Id);
        Assert.Equal("g1", stored.GoogleTaskId);
    }
}
