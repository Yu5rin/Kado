using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 点検で見つかった、予定の同期で使う人の予定を壊しうる不具合の見張り。
/// </summary>
public class EventSyncSafetyTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _remote = new();

    private EventRepository Events => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private SettingsRepository Settings => new(_connection);

    private EventSyncEngine Engine => new(Events, Tombstones, Settings, _remote);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------
    // 403 quotaExceeded
    // ------------------------------------------------------------------

    [Fact]
    public void 容量の上限の403は中身を断られたことにしない()
    {
        var error = new GoogleApiException(HttpStatusCode.Forbidden, "quotaExceeded");

        Assert.True(error.IsQuotaExceeded);
        Assert.False(error.IsRejection);
        Assert.False(error.IsPermissionDenied);

        // 権限が無い403は、これまでどおり中身を断られたもの
        Assert.True(new GoogleApiException(HttpStatusCode.Forbidden, "forbidden").IsRejection);
    }

    [Fact]
    public async Task 容量の上限で断られたら印を付けずにその回の送信を打ち切る()
    {
        for (var i = 1; i <= 3; i++)
        {
            Events.Upsert(new CalendarEvent
            {
                Id = $"e{i}", Title = $"予定{i}", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
            });
        }

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.Forbidden, "quotaExceeded");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.True(report.Deferred);

        // 1件目で打ち切る。残りを叩き続けない
        Assert.Equal(1, _remote.WriteAttempts);

        // 中身のせいではないので、どれにも「送れなかった」印は付かない
        Assert.All(Events.All(), e => Assert.Null(e.PushRejection));

        // 次回、通るようになれば全部送れる
        _remote.ThrowOnEveryWrite = null;
        var next = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(3, next.CreatedRemote);
    }

    // ------------------------------------------------------------------
    // 送信中の編集を、応答で潰さない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 新規を送っている間に保存された編集を応答で潰さない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "図面レビュー", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Events.Upsert(Events.Find("e1")! with { Title = "図面レビュー（変更）" });
        };

        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());

        // 手元の新しい中身は残り、結び付きだけが付く
        Assert.Equal("図面レビュー（変更）", stored.Title);
        Assert.NotNull(stored.GoogleEventId);
        Assert.True(EventMapper.NeedsPush(stored));

        // 次回に送る。二重には作らない
        var next = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, next.UpdatedRemote);
        Assert.Equal(0, next.CreatedRemote);
        Assert.Equal("図面レビュー（変更）", Assert.Single(_remote.Items).Value["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task 書き換えを送っている間に保存された編集を応答で潰さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Assert.Single(Events.All()) with { Title = "棚卸し（送る分）" });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Events.Upsert(Events.Find("google:g1")! with { Title = "棚卸し（送信中の編集）" });
        };

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal("棚卸し（送信中の編集）", Assert.Single(Events.All()).Title);

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal("棚卸し（送信中の編集）", Assert.Single(_remote.Items).Value["summary"]!.GetValue<string>());
        Assert.Equal("棚卸し（送信中の編集）", Assert.Single(Events.All()).Title);
    }

    [Fact]
    public async Task 送っている間に手元で消された新規は復活させず向こうの写しを消す()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "図面レビュー", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        _remote.AfterWrite = () =>
        {
            _remote.AfterWrite = null;
            Events.Delete("e1");
        };

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(Events.All());

        // 向こうにだけ残った写しは、次の同期で消す
        var next = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, next.DeletedRemote);
        Assert.Empty(_remote.Items);
        Assert.Empty(Events.All());
    }

    // ------------------------------------------------------------------
    // 開始＝終了の時刻つき予定
    // ------------------------------------------------------------------

    private void AddZeroLength()
    {
        _remote.Items["g1"] = new JsonObject
        {
            ["id"] = "g1",
            ["summary"] = "締め切り",
            ["status"] = "confirmed",
            ["updated"] = "2026-09-19T00:00:00.000Z",
            ["start"] = new JsonObject { ["dateTime"] = "2026-09-24T09:00:00+09:00", ["timeZone"] = "Asia/Tokyo" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-09-24T09:00:00+09:00", ["timeZone"] = "Asia/Tokyo" },
        };
    }

    [Fact]
    public async Task 開始と終了が同じ予定を取り込んでも編集していなければ送らない()
    {
        AddZeroLength();

        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Assert.False(EventMapper.NeedsPush(stored));

        var second = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, second.UpdatedRemote);
        Assert.Equal(
            _remote.Items["g1"]["start"]!["dateTime"]!.GetValue<string>(),
            _remote.Items["g1"]["end"]!["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task 開始と終了が同じ予定の題だけ変えても終了は動かさない()
    {
        AddZeroLength();
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Assert.Single(Events.All()) with { Title = "締め切り（変更）" });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.UpdatedRemote);
        Assert.Empty(report.Warnings);
        Assert.Equal(
            _remote.Items["g1"]["start"]!["dateTime"]!.GetValue<string>(),
            _remote.Items["g1"]["end"]!["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task 使う人が時刻を変えたときは終了を開始の1時間後に補う()
    {
        AddZeroLength();
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { StartTime = stored.StartTime!.Value.AddHours(1), EndTime = stored.StartTime.Value.AddHours(1) });

        await Engine.SyncAsync("primary", "local:shigoto");

        var item = _remote.Items["g1"];
        var start = DateTimeOffset.Parse(item["start"]!["dateTime"]!.GetValue<string>());
        var end = DateTimeOffset.Parse(item["end"]!["dateTime"]!.GetValue<string>());

        Assert.Equal(TimeSpan.FromHours(1), end - start);
    }

    // ------------------------------------------------------------------
    // 持ち主の分からない削除記録
    // ------------------------------------------------------------------

    [Fact]
    public async Task 持ち主の分からない削除記録は404なら別のカレンダーのものとして残す()
    {
        // 古い記録（source_id が NULL）。この予定は primary には無い
        Tombstones.Record("old", TombstoneRepository.EventKind, "elsewhere", DateTimeOffset.Now);

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, report.DeletedRemote);

        // API 層が 404 を飲み込むと、ここで記録が消えて、別のカレンダーへ削除が届かなくなっていた
        Assert.Equal(1, Tombstones.Count());
    }

    [Fact]
    public async Task 持ち主が分かっている削除記録は404なら片付ける()
    {
        Tombstones.Record("old", TombstoneRepository.EventKind, "elsewhere", DateTimeOffset.Now, "primary");

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, Tombstones.Count());
    }

    // ------------------------------------------------------------------
    // 全件の取り直しで、移す指示を出した行を守る
    // ------------------------------------------------------------------

    private async Task<CalendarEvent> ImportThenAssignOtherCalendarAsync()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 入れ先だけを変えた（未送信の移す指示）。送る中身は変わっていない
        var stored = Assert.Single(Events.All()) with { CalendarId = "local:other" };
        Events.Upsert(stored);
        Assert.False(EventMapper.NeedsPush(stored));

        // 元のカレンダーの一覧には無い。全件を取り直す
        _remote.Items.Remove("g1");
        Settings.SetSyncState(EventSyncEngine.TokenKey("primary"), string.Empty);

        return stored;
    }

    [Fact]
    public async Task 全件の取り直しで移す指示を出した行は一覧に無くても消さない()
    {
        await ImportThenAssignOtherCalendarAsync();

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, report.DeletedLocal);

        var kept = Assert.Single(Events.All());
        Assert.True(kept.GoogleMissing);
        Assert.Equal("local:other", kept.CalendarId);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task 全件の取り直しで移し先に確かにあれば移動は済んでいたとして場所を直す()
    {
        await ImportThenAssignOtherCalendarAsync();

        // events.move は Google で通っていて、移し先に生きている
        _remote.Located[("local:other", "g1")] = new JsonObject
        {
            ["id"] = "g1", ["summary"] = "棚卸し", ["status"] = "confirmed",
            ["updated"] = "2026-09-19T00:00:00.000Z",
            ["start"] = new JsonObject { ["date"] = "2026-09-24" },
            ["end"] = new JsonObject { ["date"] = "2026-09-25" },
        };

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, report.DeletedLocal);

        var kept = Assert.Single(Events.All());
        Assert.False(kept.GoogleMissing);
        Assert.Equal("local:other", kept.GoogleCalendarId);
    }

    // ------------------------------------------------------------------
    // 未送信の行を、別の項目と取り違えて引き受けない
    // ------------------------------------------------------------------

    [Fact]
    public async Task メモが食い違う未送信の予定は同じものとして引き受けない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
            Note = "手元にだけあるメモ",
        });

        // 題・日付は同じだが、メモが違う別の予定が向こうにある。手元の送信は止めておく
        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        _remote.Add("g1", "会議", "2026-09-24");
        _remote.Items["g1"]["description"] = "向こうの別のメモ";

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(2, Events.All().Count);
        Assert.Equal("手元にだけあるメモ", Events.Find("e1")!.Note);
    }

    [Fact]
    public async Task 場所が食い違う未送信の予定は同じものとして引き受けない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
            Location = "会議室A",
        });

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        _remote.Add("g1", "会議", "2026-09-24");
        _remote.Items["g1"]["location"] = "会議室B";

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(2, Events.All().Count);
        Assert.Equal("会議室A", Events.Find("e1")!.Location);
    }

    [Fact]
    public async Task 中身が揃っている未送信の予定は送信の途中で切れたものとして引き受ける()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
            Note = "メモ", Location = "会議室A",
        });

        _remote.ThrowOnEveryWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        _remote.Add("g1", "会議", "2026-09-24");
        _remote.Items["g1"]["description"] = "メモ";
        _remote.Items["g1"]["location"] = "会議室A";

        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Assert.Equal("e1", stored.Id);
        Assert.Equal("g1", stored.GoogleEventId);
    }

    // ------------------------------------------------------------------
    // 時刻つき→終日で timeZone を残さない
    // ------------------------------------------------------------------

    [Fact]
    public void 時刻つきを終日にするとstartとendのtimeZoneにも明示的にnullを送る()
    {
        var value = EventMapper.FromGoogle(Json("""
            {"id":"g1","summary":"会議","start":{"dateTime":"2026-11-24T09:00:00+09:00","timeZone":"Asia/Tokyo"},
             "end":{"dateTime":"2026-11-24T10:00:00+09:00","timeZone":"Asia/Tokyo"}}
            """), "primary");

        var body = EventMapper.ToGoogle(value with { StartTime = null, EndTime = null });

        foreach (var key in new[] { "start", "end" })
        {
            var node = body[key]!.AsObject();

            Assert.True(node.ContainsKey("timeZone"));
            Assert.Null(node["timeZone"]);
            Assert.True(node.ContainsKey("dateTime"));
            Assert.Null(node["dateTime"]);
            Assert.NotNull(node["date"]);
        }
    }

    [Fact]
    public void 終日を時刻つきにするときはdateだけを消す()
    {
        var value = EventMapper.FromGoogle(Json("""
            {"id":"g1","summary":"会議","start":{"date":"2026-11-24"},"end":{"date":"2026-11-25"}}
            """), "primary");

        var body = EventMapper.ToGoogle(value with { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) });

        Assert.True(body["start"]!.AsObject().ContainsKey("date"));
        Assert.Null(body["start"]!["date"]);
        Assert.NotNull(body["start"]!["timeZone"]);
    }
}
