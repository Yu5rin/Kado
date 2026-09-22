using System.Net;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// タスクの同期。
/// <para>Tasks には syncToken が無く、差分は updatedMin で取る。</para>
/// </summary>
public class TaskSyncEngineTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    /// <summary>同期の側と相手の側で共有する時計。食い違うと差分が降ってこない。</summary>
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeTaskGateway _remote;

    public TaskSyncEngineTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private SettingsRepository Settings => new(_connection);

    private TaskSyncEngine Engine => new(Tasks, Tombstones, Settings, _remote, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task 相手にだけあるタスクを取り込む()
    {
        _remote.Add("g1", "台数計画の確定", due: "2026-09-24");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.CreatedLocal);

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("台数計画の確定", stored.Title);

        // 時差で1日ずれていない
        Assert.Equal(D(2026, 9, 24), stored.Due);
        Assert.Equal("local:mytasks", stored.TaskListId);
        Assert.Equal("@default", stored.GoogleTaskListId);
    }

    [Fact]
    public async Task こちらにだけあるタスクを送る()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.CreatedRemote);
        Assert.Single(_remote.Items);
        Assert.NotNull(Tasks.All().Single().GoogleTaskId);
    }

    [Fact]
    public async Task 二度目は何も起きない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");

        await Engine.SyncAsync("@default", "local:mytasks");
        var second = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.False(second.HasChanges);
    }

    [Fact]
    public async Task 期限なしのタスクも往復できる()
    {
        _remote.Add("g1", "いつかやる");

        await Engine.SyncAsync("@default", "local:mytasks");
        var second = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Null(Tasks.All().Single().Due);

        // 期限なしを毎回「変わった」と見ない
        Assert.Equal(0, second.UpdatedRemote);
    }

    [Fact]
    public async Task 完了を取り込める()
    {
        _remote.Add("g1", "済んだ作業", due: "2026-09-24", done: true);

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.True(stored.IsDone);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task 完了を送れる()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tasks.Upsert(stored with { IsDone = true });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.UpdatedRemote);
        Assert.Equal("completed", _remote.Items["g1"]["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task 相手が消したらこちらも消す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        _remote.Delete("g1");
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Tasks.All());
    }

    [Fact]
    public async Task 完了して隠れただけなら消さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        // hidden は消したのとは別物
        _remote.Hide("g1");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.True(stored.IsDone);
    }

    [Fact]
    public async Task こちらで消したら相手でも消す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tombstones.Record(stored.Id, TombstoneRepository.TaskKind, "g1", DateTimeOffset.Now);
        Tasks.Delete(stored.Id);

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.DeletedRemote);
        Assert.Contains("g1", _remote.Deleted);
        Assert.Equal(0, Tombstones.Count());
    }

    [Fact]
    public async Task 消したタスクは取り込みで復活しない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tombstones.Record(stored.Id, TombstoneRepository.TaskKind, "g1", DateTimeOffset.Now);
        Tasks.Delete(stored.Id);

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Empty(Tasks.All());
    }

    [Fact]
    public async Task 前回いつ取りに行ったかを覚えて次に使う()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");
        await Engine.SyncAsync("@default", "local:mytasks");

        // 初回は全件、2回目は前回の時刻から
        Assert.Null(_remote.SeenSince[0]);
        Assert.NotNull(_remote.SeenSince[1]);
    }

    [Fact]
    public async Task 相手が変わっていなければ送れていない変更を潰さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tasks.Upsert(stored with { Title = "集計（変更）" });

        // 前回の時刻を消して、全件が降ってくる状態にする
        Settings.ClearSyncState();

        await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("集計（変更）", Tasks.All().Single().Title);
        Assert.Equal("集計（変更）", _remote.Items["g1"]["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task どちらも変わったら相手を採る()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tasks.Upsert(stored with { Title = "こちらの変更" });
        _remote.Edit("g1", "相手の変更");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal("相手の変更", Tasks.All().Single().Title);

        // 未送信の変更を黙って捨てていないか（EventSyncEngine と同じ理由）
        Assert.Contains(report.Warnings, w => w.Contains("こちらの変更", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 送っていない変更が無ければ警告を残さない()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        _remote.Edit("g1", "集計（相手で変更）");
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 結び付いていない同じタスクは引き受けて増やさない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });
        _remote.Add("g1", "集計", due: "2026-09-24");

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal("t1", stored.Id);
        Assert.Equal("g1", stored.GoogleTaskId);
    }

    [Fact]
    public async Task 結び付いた既存タスクの作成日時と並び順は消えない()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            CreatedAt = createdAt, SortOrder = 3,
        });
        _remote.Add("g1", "集計", due: "2026-09-24");

        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Assert.Equal(createdAt, stored.CreatedAt);
        Assert.Equal(3, stored.SortOrder);
    }

    [Fact]
    public async Task 相手にだけある新規タスクは同じ期限日の末尾に並ぶ()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "先にあったタスク", Due = D(2026, 9, 24),
            TaskListId = "local:mytasks", SortOrder = 0,
        });
        _remote.Add("g1", "相手から来たタスク", due: "2026-09-24");

        await Engine.SyncAsync("@default", "local:mytasks");

        var incoming = Tasks.All().Single(t => t.GoogleTaskId == "g1");
        Assert.Equal(1, incoming.SortOrder);
    }

    [Fact]
    public async Task 相手から消えていたら結びを外して作り直す()
    {
        _remote.Add("g1", "集計", due: "2026-09-24");
        await Engine.SyncAsync("@default", "local:mytasks");

        var stored = Assert.Single(Tasks.All());
        Tasks.Upsert(stored with { Title = "集計（変更）" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, report.Relinked);
        Assert.Null(Tasks.All().Single().GoogleTaskId);
    }

    [Fact]
    public async Task 他のリストのタスクは送らない()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "買い物", Due = D(2026, 9, 24), TaskListId = "local:kaimono",
        });

        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, report.CreatedRemote);
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 一時的な失敗では送信を諦めて知らせる()
    {
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });

        _remote.ThrowOnWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, report.CreatedRemote);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task 一般の失敗で断られても同じリストの残りは送る()
    {
        // 実機で、送れないタスクが1件あるだけで同じリストの他のタスクまで
        // 一切動かなくなった（EventSyncEngine と同じ不具合）。400 のような
        // IsMissing でも IsTransient でもない失敗で確かめる
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "断られる", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });
        Tasks.Upsert(new TaskItem
        {
            Id = "t2", Title = "送れる", Due = D(2026, 9, 25), TaskListId = "local:mytasks",
        });

        // 1回だけ投げるので、先に処理されたほうだけが断られる
        _remote.ThrowOnWrite = new GoogleApiException(HttpStatusCode.BadRequest, "invalid");
        var report = await Engine.SyncAsync("@default", "local:mytasks");

        // 断られなかったほうは、続けて送られている
        Assert.Equal(1, report.CreatedRemote);
        Assert.Single(_remote.Items);
        Assert.Single(report.Warnings);
    }
}
