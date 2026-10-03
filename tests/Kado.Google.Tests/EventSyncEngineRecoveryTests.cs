using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 予定の同期が止まる・食い違う筋。
/// <para>
/// 削除の送信で 4xx が出ても同期を止めない／全件を取り直したときに Google で消えた予定を拾う／
/// Google では移せない予定の入れ先を変えさせない／例外回のために足した除外日を失わない。
/// </para>
/// </summary>
public class EventSyncEngineRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _remote = new();

    private EventRepository Events => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private EventSyncEngine Engine => new(Events, Tombstones, new SettingsRepository(_connection), _remote);

    public void Dispose() => _connection.Dispose();

    private static GoogleApiException Api(HttpStatusCode status, string reason) => new(status, reason);

    // ------------------------------------------------------------------
    // 削除の送信で 4xx が出ても、そのカレンダーの同期を止めない
    // ------------------------------------------------------------------

    private void RecordDeletion(string localId, string googleId) =>
        Tombstones.Record(localId, TombstoneRepository.EventKind, googleId, DateTimeOffset.Now, "primary");

    [Fact]
    public async Task 削除が400で断られても警告にして記録を残し続きを進める()
    {
        _remote.Add("g1", "消す予定", "2026-09-24");
        _remote.Add("g2", "別の予定", "2026-09-25");
        await Engine.SyncAsync("primary", "primary");

        // 別の予定は編集してあり、これは送る必要がある
        Events.Upsert(Events.All().Single(e => e.GoogleEventId == "g2") with { Title = "別の予定（変更）" });

        // 削除を送ろうとして 400 が返る
        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.BadRequest, "invalid");

        var report = await Engine.SyncAsync("primary", "primary");

        // 同期は止まらず、ほかの送信は続く
        Assert.Equal("別の予定（変更）", (string?)_remote.Items["g2"]["summary"]);
        Assert.Equal(1, report.UpdatedRemote);

        // その削除だけが警告になり、次回やり直すので記録は残る
        Assert.Contains(report.Warnings, w => w.Contains("削除を伝えられませんでした", StringComparison.Ordinal));
        Assert.NotNull(Tombstones.Find("gone-local", TombstoneRepository.EventKind));
    }

    [Fact]
    public async Task 削除が権限で403なら記録を捨てて警告する()
    {
        _remote.Add("g1", "消す予定", "2026-09-24");
        await Engine.SyncAsync("primary", "primary");

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.Forbidden, "forbiddenForNonOrganizer");

        var report = await Engine.SyncAsync("primary", "primary");

        // 何度送っても通らない。記録を残すと毎回失敗する
        Assert.Null(Tombstones.Find("gone-local", TombstoneRepository.EventKind));
        Assert.Contains(report.Warnings, w => w.Contains("削除できません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 削除が呼びすぎの403なら記録を残す()
    {
        _remote.Add("g1", "消す予定", "2026-09-24");
        await Engine.SyncAsync("primary", "primary");

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.Forbidden, "rateLimitExceeded");

        await Engine.SyncAsync("primary", "primary");

        Assert.NotNull(Tombstones.Find("gone-local", TombstoneRepository.EventKind));
    }

    [Fact]
    public async Task 削除が410ならすでに消えているので記録を片付ける()
    {
        _remote.Add("g1", "消す予定", "2026-09-24");
        await Engine.SyncAsync("primary", "primary");

        RecordDeletion("gone-local", "g1");
        _remote.ThrowOnWrite = Api(HttpStatusCode.Gone, "deleted");

        var report = await Engine.SyncAsync("primary", "primary");

        Assert.Null(Tombstones.Find("gone-local", TombstoneRepository.EventKind));
        Assert.Empty(report.Warnings);
    }

    // ------------------------------------------------------------------
    // 全件を取り直すときに、Google で消えた予定を拾う
    // ------------------------------------------------------------------

    [Fact]
    public async Task 差分の印が古くなって全件を取り直すと間に消えた予定を手元からも消す()
    {
        _remote.Add("g1", "残る予定", "2026-09-24");
        _remote.Add("g2", "間に消えた予定", "2026-09-25");
        await Engine.SyncAsync("primary", "primary");

        // Google で消されたが、差分の印が古くなり cancelled は降ってこない
        _remote.Items.Remove("g2");
        _remote.FailNextWithGone = true;

        var report = await Engine.SyncAsync("primary", "primary");

        Assert.True(report.FullResync);
        Assert.Equal(["g1"], Events.All().Select(e => e.GoogleEventId!).ToArray());
        Assert.Equal(1, report.DeletedLocal);

        // 手元から消すだけ。Google へ削除を送らない
        Assert.Empty(_remote.Deleted);
        Assert.Equal(0, Tombstones.Count());
    }

    [Fact]
    public async Task 全件の取り直しで消えていた予定に未送信の変更があれば残して印と警告を出す()
    {
        _remote.Add("g2", "間に消えた予定", "2026-09-25");
        await Engine.SyncAsync("primary", "primary");

        Events.Upsert(Events.All().Single() with { Title = "手元で直した" });
        _remote.Items.Remove("g2");
        _remote.FailNextWithGone = true;

        var report = await Engine.SyncAsync("primary", "primary");

        var kept = Assert.Single(Events.All());
        Assert.Equal("手元で直した", kept.Title);
        Assert.True(kept.GoogleMissing);
        Assert.Equal(0, report.DeletedLocal);
        Assert.Contains(report.Warnings, w => w.Contains("手元で直した", StringComparison.Ordinal));

        // 作り直さない（二重にしない）
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 繋ぎ直しで印を捨てた全件取得でも消えた予定を拾う()
    {
        _remote.Add("g1", "消える予定", "2026-09-24");
        await Engine.SyncAsync("primary", "primary");

        // 一覧に戻ってきたカレンダーは、印を空にして全件を取り直す
        new SettingsRepository(_connection).SetSyncState(EventSyncEngine.TokenKey("primary"), string.Empty);
        _remote.Items.Remove("g1");

        await Engine.SyncAsync("primary", "primary");

        Assert.Empty(Events.All());
    }

    [Fact]
    public async Task 差分の取り込みでは手元にだけある結び付いた予定を消さない()
    {
        _remote.Add("g1", "予定", "2026-09-24");
        await Engine.SyncAsync("primary", "primary");

        // 差分には何も載らない（変わっていない）。一覧に無いのは「消えた」ではない
        await Engine.SyncAsync("primary", "primary");

        Assert.Single(Events.All());
    }

    [Fact]
    public async Task 取り込んでいない期間の予定は全件の取り直しで消さない()
    {
        _remote.Add("old", "去年の予定", "2025-03-10");
        _remote.Add("new", "今の予定", "2026-09-24");
        _remote.Add("gone", "間に消えた予定", "2026-09-25");
        await Engine.SyncAsync("primary", "primary");

        // 一覧は 2026-01-01 以降だけを取る。古い予定は Google にあっても一覧に載らない
        _remote.ListFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _remote.Items.Remove("old");
        _remote.Items.Remove("gone");
        _remote.FailNextWithGone = true;

        await Engine.SyncAsync("primary", "primary");

        var ids = Events.All().Select(e => e.GoogleEventId!).Order().ToArray();
        Assert.Equal(["new", "old"], ids);
    }

    [Fact]
    public async Task 繰り返しの予定を含めても全件の取り直しで誤って消さない()
    {
        _remote.AddRecurring("r1", "週次", "2024-01-04", "FREQ=WEEKLY;BYDAY=TH");
        await Engine.SyncAsync("primary", "primary");

        _remote.FailNextWithGone = true;
        await Engine.SyncAsync("primary", "primary");

        Assert.Single(Events.All());
    }

    // ------------------------------------------------------------------
    // Google では移せない予定は、移そうとしない
    // ------------------------------------------------------------------

    private async Task<CalendarEvent> SeedMoveRequestAsync(Action<JsonObject>? shape = null)
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        shape?.Invoke(_remote.Items["g1"]);
        await Engine.SyncAsync("cal-a", "cal-a");

        // 題を直し、入れ先も変えた
        Events.Upsert(Events.All().Single() with { CalendarId = "cal-b", Title = "棚卸し（変更）" });

        return Events.All().Single();
    }

    [Fact]
    public async Task 移すのが断られても内容の変更は送り入れ先の希望を元に戻す()
    {
        await SeedMoveRequestAsync();
        _remote.ThrowOnWrite = Api(HttpStatusCode.Forbidden, "forbiddenForNonOrganizer");

        var report = await Engine.SyncAsync("cal-b", "cal-b");

        // 内容は PATCH で送られる
        Assert.Equal("棚卸し（変更）", (string?)_remote.Items["g1"]["summary"]);
        Assert.Equal(1, report.UpdatedRemote);

        // 希望は元に戻り、移せなかったことが警告に出る
        var after = Assert.Single(Events.All());
        Assert.Equal("cal-a", after.CalendarId);
        Assert.Equal("cal-a", after.GoogleCalendarId);
        Assert.Contains(report.Warnings, w => w.Contains("移せませんでした", StringComparison.Ordinal));

        // 次の同期で同じ失敗を繰り返さない
        var again = await Engine.SyncAsync("cal-a", "cal-a");
        Assert.Empty(again.Warnings);
        Assert.Empty(_remote.Moved);
    }

    [Fact]
    public async Task 他人が主催する予定は移さず内容だけを送る()
    {
        // guestsCanModify が立っていても、move は主催者しかできない
        await SeedMoveRequestAsync(item =>
        {
            item["organizer"] = new JsonObject { ["self"] = false, ["email"] = "boss@example.com" };
            item["guestsCanModify"] = true;
        });

        var report = await Engine.SyncAsync("cal-b", "cal-b");

        Assert.Empty(_remote.Moved);
        Assert.Equal("cal-a", Events.All().Single().CalendarId);
        Assert.Contains(report.Warnings, w => w.Contains("移せません", StringComparison.Ordinal));
        Assert.Equal("棚卸し（変更）", (string?)_remote.Items["g1"]["summary"]);
    }

    [Theory]
    [InlineData("focusTime")]
    [InlineData("outOfOffice")]
    [InlineData("workingLocation")]
    [InlineData("fromGmail")]
    public async Task defaultでない種類の予定は移さない(string eventType)
    {
        await SeedMoveRequestAsync(item => item["eventType"] = eventType);

        await Engine.SyncAsync("cal-b", "cal-b");

        Assert.Empty(_remote.Moved);
        Assert.Equal("cal-a", Events.All().Single().CalendarId);
    }

    [Fact]
    public async Task defaultの種類で主催者なら今までどおり移す()
    {
        await SeedMoveRequestAsync(item =>
        {
            item["eventType"] = "default";
            item["organizer"] = new JsonObject { ["self"] = true };
        });

        var report = await Engine.SyncAsync("cal-b", "cal-b");

        Assert.Single(_remote.Moved);
        Assert.Equal(1, report.Moved);
    }

    // ------------------------------------------------------------------
    // 例外回のために手元で足した除外日を、親を送ったあとも失わない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 親を送った応答の取り込みで例外回の除外日が消えない()
    {
        _remote.AddRecurring("p1", "週次", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("e1", "p1", "2026-10-01", newDate: "2026-10-02");
        await Engine.SyncAsync("primary", "primary");

        var parent = Events.All().Single(e => e.GoogleEventId == "p1");
        Assert.Contains("EXDATE=20261001", parent.Recurrence);

        // 親の題を直して送る
        Events.Upsert(parent with { Title = "週次（変更）" });
        await Engine.SyncAsync("primary", "primary");

        var after = Events.All().Single(e => e.GoogleEventId == "p1");
        Assert.Equal("週次（変更）", after.Title);

        // 動かした回が元の日にも出ない（除外が残っている）
        Assert.Contains("EXDATE=20261001", after.Recurrence);

        // 手元で足した除外日は Google へ送っていない
        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=TH"],
            _remote.Items["p1"]["recurrence"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }
}
