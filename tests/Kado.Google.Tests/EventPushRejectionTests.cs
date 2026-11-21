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
/// 同期のたびに「送れませんでした（…）: invalid — Invalid start time.」が出続けた不具合。
/// <para>
/// 原因は2つ。(1) 終日⇔時刻つきに変えた予定を patch で送ると、元の形の項目（<c>dateTime</c> / <c>date</c>）が
/// 混ぜ合わせで残り、開始に <c>date</c> と <c>dateTime</c> が両方ある形になって Google が断る。
/// (2) 断られた予定を、同期のたびに送り直して毎回警告に出していた。
/// </para>
/// <para>
/// 直し方：(1) 元の形の項目を明示的に null で消す。明らかにおかしい中身は送る前に止める。
/// (2) 断られたら印を付け、中身が変わるまで送らず、警告は印を付けた同期の1回だけ。
/// </para>
/// </summary>
public class EventPushRejectionTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _remote = new();

    private EventRepository Events => new(_connection);

    private EventSyncEngine Engine =>
        new(Events, new TombstoneRepository(_connection), new SettingsRepository(_connection), _remote);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static GoogleApiException Invalid() =>
        new(HttpStatusCode.BadRequest, "invalid", """{"error":{"message":"Invalid start time."}}""");

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------
    // 原因：終日⇔時刻つきの切り替え
    // ------------------------------------------------------------------

    [Fact]
    public async Task 終日の予定に時刻を付けて送ってもInvalidStartTimeにならない()
    {
        _remote.Add("g1", "27単計 部内審議", "2026-11-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 直す前は、開始に date と dateTime が両方残って 400 invalid になっていた
        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.UpdatedRemote);

        var start = _remote.Items["g1"]["start"]!.AsObject();
        Assert.False(start.ContainsKey("date"));
        Assert.True(start.ContainsKey("dateTime"));
        Assert.False(_remote.Items["g1"]["end"]!.AsObject().ContainsKey("date"));
        Assert.Null(Assert.Single(Events.All()).PushRejection);
    }

    [Fact]
    public async Task 時刻つきの予定を終日にして送ってもInvalidStartTimeにならない()
    {
        _remote.Items["g1"] = new JsonObject
        {
            ["id"] = "g1",
            ["summary"] = "27単計 部内審議",
            ["status"] = "confirmed",
            ["updated"] = "2026-09-19T00:00:00.000Z",
            ["start"] = new JsonObject { ["dateTime"] = "2026-11-24T09:00:00+09:00", ["timeZone"] = "Asia/Tokyo" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-11-24T10:00:00+09:00", ["timeZone"] = "Asia/Tokyo" },
        };
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { StartTime = null, EndTime = null });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.UpdatedRemote);
        Assert.False(_remote.Items["g1"]["start"]!.AsObject().ContainsKey("dateTime"));
        Assert.True(_remote.Items["g1"]["start"]!.AsObject().ContainsKey("date"));
        Assert.False(_remote.Items["g1"]["end"]!.AsObject().ContainsKey("dateTime"));
    }

    [Fact]
    public void 形が変わらないときは余計なnullを足さない()
    {
        var raw = Json("""
            {"id":"g1","summary":"会議","start":{"dateTime":"2026-11-24T09:00:00+09:00","timeZone":"Asia/Tokyo"},
             "end":{"dateTime":"2026-11-24T10:00:00+09:00","timeZone":"Asia/Tokyo"}}
            """);
        var value = EventMapper.FromGoogle(raw, "primary");

        var body = EventMapper.ToGoogle(value with { Title = "会議（変更）" });

        Assert.False(body["start"]!.AsObject().ContainsKey("date"));
        Assert.False(body["end"]!.AsObject().ContainsKey("date"));
    }

    // ------------------------------------------------------------------
    // 送る前の確認
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1899, 12, 31)]
    [InlineData(2201, 1, 1)]
    public void 異常な年の開始日は送れない扱いにする(int year, int month, int day)
    {
        var value = new CalendarEvent { Id = "e1", Title = "壊れた日付", Date = D(year, month, day) };

        var check = EventMapper.CheckOutgoing(value with { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) });

        Assert.NotNull(check.Problem);
        Assert.Contains("不正", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 終日の既定の日付も送れない扱いにする()
    {
        var check = EventMapper.CheckOutgoing(new CalendarEvent { Id = "e1", Title = "壊れた日付" });

        Assert.NotNull(check.Problem);
    }

    [Fact]
    public void ふつうの予定は送れる()
    {
        var timed = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 11, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        };
        var allDay = new CalendarEvent { Id = "e2", Title = "出張", Date = D(2026, 11, 24), EndDate = D(2026, 11, 26) };

        Assert.Null(EventMapper.CheckOutgoing(timed).Problem);
        Assert.Null(EventMapper.CheckOutgoing(allDay).Problem);
    }

    [Fact]
    public async Task 不正な日付の予定は送らず一回だけ知らせる()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "27単計 部内審議", CalendarId = "local:shigoto",
            Date = default, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        var first = await Engine.SyncAsync("primary", "local:shigoto");

        // 送っていない（Google に断られるまでもない）
        Assert.Equal(0, _remote.WriteAttempts);
        var warning = Assert.Single(first.Warnings);
        Assert.Contains("27単計 部内審議", warning, StringComparison.Ordinal);
        Assert.Contains("手元にだけ残しています", warning, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(Events.All()).PushRejection);

        // 2回目以降は黙る
        var second = await Engine.SyncAsync("primary", "local:shigoto");
        Assert.Empty(second.Warnings);
        Assert.Equal(0, _remote.WriteAttempts);
    }

    // ------------------------------------------------------------------
    // 断られた予定は、1回だけ知らせて、毎回は言わない
    // ------------------------------------------------------------------

    private async Task<CalendarEvent> SeedRejectedAsync()
    {
        _remote.Add("g1", "27単計 部内審議", "2026-11-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Assert.Single(Events.All()) with { Title = "27単計 部内審議（変更）" });
        _remote.ThrowOnEveryWrite = Invalid();

        return Assert.Single(Events.All());
    }

    [Fact]
    public async Task 断られた予定には印を付け警告は一回だけ出す()
    {
        await SeedRejectedAsync();

        var first = await Engine.SyncAsync("primary", "local:shigoto");

        var warning = Assert.Single(first.Warnings);
        Assert.Equal(
            "Google に送れませんでした（27単計 部内審議（変更））：invalid — Invalid start time.。" +
            "手元にだけ残しています。直すと送り直します",
            warning);

        var marked = Assert.Single(Events.All());
        Assert.Equal("invalid — Invalid start time.", marked.PushRejection);
        Assert.False(string.IsNullOrEmpty(marked.PushRejectionFingerprint));

        // 2回目以降：送り直さず、警告も出さない
        var attempts = _remote.WriteAttempts;
        for (var i = 0; i < 3; i++)
        {
            var again = await Engine.SyncAsync("primary", "local:shigoto");
            Assert.Empty(again.Warnings);
        }

        Assert.Equal(attempts, _remote.WriteAttempts);
        Assert.Equal("27単計 部内審議（変更）", Assert.Single(Events.All()).Title);
    }

    [Fact]
    public async Task 断られたときは理由と送ろうとした日時を記録の行に残す()
    {
        var stored = await SeedRejectedAsync();
        Events.Upsert(stored with { Note = "秘密のメモ", Location = "秘密の場所" });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        var line = Assert.Single(report.LogLines);
        Assert.Contains("27単計 部内審議（変更）", line, StringComparison.Ordinal);
        Assert.Contains("invalid — Invalid start time.", line, StringComparison.Ordinal);
        Assert.Contains("start=", line, StringComparison.Ordinal);
        Assert.Contains("end=", line, StringComparison.Ordinal);
        Assert.Contains("Date=2026-11-24", line, StringComparison.Ordinal);
        Assert.Contains("IsAllDay=True", line, StringComparison.Ordinal);
        Assert.Contains("recurringEventId=なし", line, StringComparison.Ordinal);

        // メモや場所の中身は書かない
        Assert.DoesNotContain("秘密", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 中身を直すともう一度送り送れたら印を外す()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("primary", "local:shigoto");

        // 直した（題名を変えた）。Google も受け付けるようになった
        _remote.ThrowOnEveryWrite = null;
        Events.Upsert(Assert.Single(Events.All()) with { Title = "27単計 部内審議（直した）" });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.UpdatedRemote);
        Assert.Equal("27単計 部内審議（直した）", _remote.Items["g1"]["summary"]!.GetValue<string>());

        var after = Assert.Single(Events.All());
        Assert.Null(after.PushRejection);
        Assert.Null(after.PushRejectionFingerprint);
    }

    [Fact]
    public async Task 直してもまた断られたら一回だけ知らせる()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Assert.Single(Events.All()) with { Title = "27単計 部内審議（直したつもり）" });

        var second = await Engine.SyncAsync("primary", "local:shigoto");
        var third = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Single(second.Warnings);
        Assert.Empty(third.Warnings);
    }

    [Fact]
    public async Task 印を外すと次の同期でもう一度送る()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("primary", "local:shigoto");

        var attempts = _remote.WriteAttempts;
        Events.Upsert(Assert.Single(Events.All()) with { PushRejection = null, PushRejectionFingerprint = null });
        _remote.ThrowOnEveryWrite = null;

        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.True(_remote.WriteAttempts > attempts);
        Assert.Equal("27単計 部内審議（変更）", _remote.Items["g1"]["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task Googleから新しい姿を受け取ると印を外す()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.ThrowOnEveryWrite = null;
        _remote.Edit("g1", "Google 側で直した題名");

        await Engine.SyncAsync("primary", "local:shigoto");

        var after = Assert.Single(Events.All());
        Assert.Null(after.PushRejection);
        Assert.Null(after.PushRejectionFingerprint);
        Assert.Equal("Google 側で直した題名", after.Title);
    }

    [Fact]
    public async Task 全件の取り直しで同じ姿が降ってきても印は残り警告は増えない()
    {
        await SeedRejectedAsync();
        await Engine.SyncAsync("primary", "local:shigoto");

        // 差分の印を捨てて全件を取り直す（410 のあとなど）
        _remote.FailNextWithGone = true;
        var attempts = _remote.WriteAttempts;

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.NotNull(Assert.Single(Events.All()).PushRejection);
        Assert.Equal(attempts, _remote.WriteAttempts);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("送れませんでした", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError")]
    public async Task 一時的な失敗には印を付けず次回にまわす(HttpStatusCode status, string reason)
    {
        await SeedRejectedAsync();
        _remote.ThrowOnEveryWrite = new GoogleApiException(status, reason);

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.True(report.Deferred);
        Assert.Null(Assert.Single(Events.All()).PushRejection);

        _remote.ThrowOnEveryWrite = null;
        var next = await Engine.SyncAsync("primary", "local:shigoto");
        Assert.Equal(1, next.UpdatedRemote);
    }

    [Fact]
    public async Task 権限が無くて断られたときも印を付けて一回だけ知らせる()
    {
        await SeedRejectedAsync();
        _remote.ThrowOnEveryWrite = new GoogleApiException(
            HttpStatusCode.Forbidden, "forbiddenForNonOrganizer", """{"error":{"message":"Only the organizer can change this."}}""");

        var first = await Engine.SyncAsync("primary", "local:shigoto");
        var second = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Single(first.Warnings);
        Assert.Contains("forbiddenForNonOrganizer — Only the organizer can change this.", first.Warnings[0], StringComparison.Ordinal);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public async Task 新しく作る予定を断られたときも印を付けて繰り返さない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "新しい予定", CalendarId = "local:shigoto", Date = D(2026, 11, 24),
        });
        _remote.ThrowOnEveryWrite = Invalid();

        var first = await Engine.SyncAsync("primary", "local:shigoto");
        var attempts = _remote.WriteAttempts;
        var second = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Single(first.Warnings);
        Assert.Empty(second.Warnings);
        Assert.Equal(attempts, _remote.WriteAttempts);
        Assert.Null(Assert.Single(Events.All()).GoogleEventId);
    }

    // ------------------------------------------------------------------
    // 添付
    // ------------------------------------------------------------------

    [Fact]
    public async Task 添付だけ断られたときは他の項目を送り添付のことを一回だけ知らせる()
    {
        _remote.Add("g1", "資料の確認", "2026-11-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var attachment = new EventAttachment("f1", "https://drive.google.com/file/d/f1", "仕様書.pdf", "application/pdf", null);
        Events.Upsert(Assert.Single(Events.All()) with
        {
            Title = "資料の確認（添付あり）",
            PendingAttachments = EventMapper.ToPendingAttachmentsJson([attachment]),
        });
        _remote.RefuseAttachments = true;

        var first = await Engine.SyncAsync("primary", "local:shigoto");

        // ほかの項目（題名）は送れている
        Assert.Equal("資料の確認（添付あり）", _remote.Items["g1"]["summary"]!.GetValue<string>());
        Assert.False(_remote.Items["g1"].AsObject().ContainsKey("attachments"));
        Assert.Equal(1, first.UpdatedRemote);

        var warning = Assert.Single(first.Warnings);
        Assert.Contains("添付だけ", warning, StringComparison.Ordinal);
        Assert.Contains("forbidden", warning, StringComparison.Ordinal);

        // 以後は同じ添付で繰り返さない
        var after = Assert.Single(Events.All());
        Assert.Null(after.PendingAttachments);
        Assert.Null(after.PushRejection);

        var second = await Engine.SyncAsync("primary", "local:shigoto");
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public async Task 添付つきで送れたときは警告を出さない()
    {
        _remote.Add("g1", "資料の確認", "2026-11-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var attachment = new EventAttachment("f1", "https://drive.google.com/file/d/f1", "仕様書.pdf", "application/pdf", null);
        Events.Upsert(Assert.Single(Events.All()) with
        {
            PendingAttachments = EventMapper.ToPendingAttachmentsJson([attachment]),
        });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(report.Warnings);
        Assert.True(_remote.Items["g1"].AsObject().ContainsKey("attachments"));
    }

    [Fact]
    public async Task このPCだけのカレンダーの予定に添付を付けても同期は触らず警告も出ない()
    {
        var attachment = new EventAttachment("f1", "https://drive.google.com/file/d/f1", "仕様書.pdf", "application/pdf", null);
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "この PC だけの予定", CalendarId = "local:private", Date = D(2026, 11, 24),
            PendingAttachments = EventMapper.ToPendingAttachmentsJson([attachment]),
        });
        _remote.RefuseAttachments = true;

        // 同期するのは Google と結び付いた別のカレンダー。この PC だけのカレンダーの予定は送らない
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(report.Warnings);
        Assert.Equal(0, _remote.WriteAttempts);
    }

    // ------------------------------------------------------------------
    // Kado だけの項目は、そもそも送らない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 色と通知の指定を変えても送らず警告も出ない()
    {
        _remote.Add("g1", "棚卸し", "2026-11-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 断られる状態にしておく。送ろうとすればすぐ分かる
        _remote.ThrowOnEveryWrite = Invalid();
        var attempts = _remote.WriteAttempts;

        Events.Upsert(Assert.Single(Events.All()) with { Color = "#ff0000", Notify = true });
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(report.Warnings);
        Assert.Equal(attempts, _remote.WriteAttempts);
        Assert.False(EventMapper.ToGoogle(Assert.Single(Events.All())).ContainsKey("colorId"));
    }

    // ------------------------------------------------------------------
    // 断られ方の見分けと、指紋
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid", true)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", true)]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", false)]
    [InlineData(HttpStatusCode.NotFound, "notFound", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded", false)]
    [InlineData(HttpStatusCode.InternalServerError, "backendError", false)]
    [InlineData(HttpStatusCode.Unauthorized, "authError", false)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, "proxy", false)]
    [InlineData(HttpStatusCode.Gone, "deleted", false)]
    public void 印を付ける断られ方は一時的でない4xxだけ(HttpStatusCode status, string reason, bool expected)
    {
        Assert.Equal(expected, new GoogleApiException(status, reason).IsRejection);
    }

    [Fact]
    public void 指紋は中身が同じなら同じで直すと変わる()
    {
        var value = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 11, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), CalendarId = "local:shigoto",
        };

        Assert.Equal(EventMapper.PushFingerprint(value), EventMapper.PushFingerprint(value with { }));
        Assert.NotEqual(EventMapper.PushFingerprint(value), EventMapper.PushFingerprint(value with { Title = "会議2" }));
        Assert.NotEqual(EventMapper.PushFingerprint(value), EventMapper.PushFingerprint(value with { EndTime = new TimeOnly(11, 0) }));

        // Kado だけの項目（色・通知）を変えても指紋は変わらない（送る本文に入らないため）
        Assert.Equal(
            EventMapper.PushFingerprint(value), EventMapper.PushFingerprint(value with { Color = "#ff0000", Notify = true }));
    }
}
