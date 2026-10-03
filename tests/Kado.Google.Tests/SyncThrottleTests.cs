using System.Net;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// Google が混み合っていて、待って出し直してもだめだったときの、同期の止まり方。
/// <para>
/// 以前は、1件が一時的に断られても残りを1件ずつ叩き続け、失敗した分だけ警告を並べていた。
/// 混んでいるのに叩き続けると、かえって混む。<b>そのカレンダーの残りは次回に回し</b>、
/// 「混み合っていたので一部を次回に回しました」と1行だけ伝える。手元は壊れていない。
/// </para>
/// </summary>
public class SyncThrottleTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _events = new();

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeTaskGateway _tasks;

    public SyncThrottleTests() => _tasks = new FakeTaskGateway(_clock);

    public void Dispose() => _connection.Dispose();

    private EventRepository Events => new(_connection);

    private TaskRepository Tasks => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private SettingsRepository Settings => new(_connection);

    private EventSyncEngine EventEngine => new(Events, Tombstones, Settings, _events);

    private TaskSyncEngine TaskEngine => new(Tasks, Tombstones, Settings, _tasks, _clock);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private void AddUnsentEvents(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Events.Upsert(new CalendarEvent
            {
                Id = $"e{i}", Title = $"予定{i}", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
            });
        }
    }

    // ------------------------------------------------------------------
    // 予定
    // ------------------------------------------------------------------

    [Fact]
    public async Task 呼びすぎなら残りの予定は叩かず次回に回す()
    {
        AddUnsentEvents(3);
        _events.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");

        var report = await EventEngine.SyncAsync("primary", "local:shigoto");

        // 1件目で止まる。残り2件は試さない
        Assert.Equal(1, _events.WriteAttempts);
        Assert.Equal(0, report.CreatedRemote);

        // 警告は1行だけ。件数ぶん並べない
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);
        Assert.True(report.Deferred);
        Assert.True(report.Throttled);

        // 手元は壊れていない。まだ送れていないだけで、次の同期でまた送る
        Assert.All(Events.All(), e => Assert.Null(e.GoogleEventId));
        Assert.Equal(3, Events.All().Count);
    }

    [Fact]
    public async Task 次の同期で残りを送る()
    {
        AddUnsentEvents(2);
        _events.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");
        await EventEngine.SyncAsync("primary", "local:shigoto");

        // 空いた
        _events.ThrowOnEveryWrite = null;
        var report = await EventEngine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(2, report.CreatedRemote);
        Assert.False(report.Throttled);
        Assert.All(Events.All(), e => Assert.NotNull(e.GoogleEventId));
    }

    [Fact]
    public async Task Googleの不調でも残りは次回に回すが間隔を延ばす理由にはしない()
    {
        // 5xx は「呼びすぎ」ではない。1つのカレンダーの不調で、裏の同期の間隔まで延ばさない
        AddUnsentEvents(3);
        _events.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");

        var report = await EventEngine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, _events.WriteAttempts);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);
        Assert.True(report.Deferred);
        Assert.False(report.Throttled);
    }

    [Fact]
    public async Task 削除を伝えるところで止まったらこのカレンダーの残りは何もしない()
    {
        // 削除の記録は残る（次の同期でまた伝える）。続けて取り込み・送信に進むと、混んでいる中でさらに叩く
        Tombstones.Record("e-old", TombstoneRepository.EventKind, "g-old", DateTimeOffset.Now, "primary");
        AddUnsentEvents(2);
        _events.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");

        var report = await EventEngine.SyncAsync("primary", "local:shigoto");

        // 削除を試した1回だけ。送信には進まない
        Assert.Equal(1, _events.WriteAttempts);
        Assert.Equal(1, Tombstones.Count());
        Assert.True(report.Throttled);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);

        // 取り込みにも進んでいない（差分の印を読みに行っていない）
        Assert.Empty(_events.SeenSyncTokens);
    }

    [Fact]
    public async Task 一般の失敗は従来どおり残りを続ける()
    {
        // 400 のような、待っても直らない失敗は、その1件だけ警告にして残りを送る
        AddUnsentEvents(2);
        _events.ThrowOnWrite = new GoogleApiException(HttpStatusCode.BadRequest, "invalid");

        var report = await EventEngine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.CreatedRemote);
        Assert.False(report.Throttled);
        Assert.NotEmpty(report.Warnings);
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    [Fact]
    public async Task 呼びすぎなら残りのタスクは叩かず次回に回す()
    {
        for (var i = 1; i <= 3; i++)
        {
            Tasks.Upsert(new TaskItem
            {
                Id = $"t{i}", Title = $"集計{i}", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            });
        }

        _tasks.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");

        var report = await TaskEngine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, _tasks.WriteAttempts);
        Assert.Equal([SyncReport.BusyWarning], report.Warnings);
        Assert.True(report.Throttled);
        Assert.Equal(3, Tasks.All().Count(t => t.GoogleTaskId is null));
    }

    [Fact]
    public async Task タスクの削除を伝えるところで止まったら残りは何もしない()
    {
        Tombstones.Record("t-old", TombstoneRepository.TaskKind, "g-old", DateTimeOffset.Now, "@default");
        Tasks.Upsert(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
        });

        _tasks.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");

        var report = await TaskEngine.SyncAsync("@default", "local:mytasks");

        Assert.Equal(1, _tasks.WriteAttempts);
        Assert.Equal(1, Tombstones.Count());
        Assert.True(report.Throttled);
    }

    // ------------------------------------------------------------------
    // 結果の足し算
    // ------------------------------------------------------------------

    [Fact]
    public void 呼びすぎの印は足し合わせても残る()
    {
        var total = new SyncReport() + new SyncReport { Deferred = true, Throttled = true } + new SyncReport();

        Assert.True(total.Deferred);
        Assert.True(total.Throttled);
        Assert.False((new SyncReport() + new SyncReport()).Deferred);
        Assert.False((new SyncReport() + new SyncReport()).Throttled);
    }
}
