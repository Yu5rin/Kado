using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// 予定が二重になる・消える筋（点検で見つかったもの）。
/// <para>
/// 404 で黙って作り直さない、未送信の編集を捨てるときは知らせる、移す指示の途中で切れても
/// 消さない、結び付いていない同じものを探す範囲を絞る。
/// </para>
/// </summary>
public class EventSyncEngineLossTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _remote = new();

    private EventRepository Events => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private EventSyncEngine Engine => new(Events, Tombstones, new SettingsRepository(_connection), _remote);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------------
    // 404 で黙って作り直さない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 送ろうとして404ならGoogleに無い印を付けて作り直さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "棚卸し（変更）" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 結び付きを外すと、次の同期で insert して二重になる（ゲスト・会議 URL・添付の無い写し）。
        // 外さず、印だけを付ける
        var after = Assert.Single(Events.All());
        Assert.Equal("g1", after.GoogleEventId);
        Assert.True(after.GoogleMissing);
        Assert.Equal(0, report.Relinked);
        Assert.Empty(_remote.Items);

        // 黙らせない。どの予定か分かるようにする
        Assert.Contains(report.Warnings, w => w.Contains("棚卸し（変更）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 印が付いた予定は以後送らない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Events.All().Single() with { Title = "棚卸し（変更）" });
        _remote.Items.Remove("g1");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 印のあとで、さらに編集しても送らない。insert（作り直し）もしない
        Events.Upsert(Events.All().Single() with { Title = "棚卸し（さらに変更）" });
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Empty(_remote.Items);
        Assert.Equal(0, report.CreatedRemote);
        Assert.Equal(0, report.UpdatedRemote);

        // 毎回の同期で同じ警告を出し続けない（編集画面が教える）
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 移す指示が404でも作り直さず印を付ける()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        Events.Upsert(Events.All().Single() with { CalendarId = "cal-b" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("cal-b", "cal-b");

        var after = Assert.Single(Events.All());
        Assert.True(after.GoogleMissing);
        Assert.Equal("g1", after.GoogleEventId);
        Assert.Equal("cal-a", after.GoogleCalendarId);
        Assert.Empty(_remote.Items);
        Assert.Contains(report.Warnings, w => w.Contains("棚卸し", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 別のカレンダーで見つかったら印を外して結び直す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        Events.Upsert(Events.All().Single() with { Title = "棚卸し（変更）" });
        _remote.Items.Remove("g1");
        await Engine.SyncAsync("cal-a", "cal-a");
        Assert.True(Events.All().Single().GoogleMissing);

        // 実は同僚が別のカレンダーへ移していた
        _remote.Add("g1", "棚卸し（Bにいた）", "2026-09-24");
        await Engine.SyncAsync("cal-b", "cal-b");

        var after = Assert.Single(Events.All());
        Assert.False(after.GoogleMissing);
        Assert.Equal("g1", after.GoogleEventId);
        Assert.Equal("cal-b", after.GoogleCalendarId);
        Assert.Equal("cal-b", after.CalendarId);
    }

    [Fact]
    public async Task 内容が同じまま見つかっても印は外れる()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        // 印だけを付ける（内容は Google の控えのまま）
        Events.Upsert(Events.All().Single() with { GoogleMissing = true });

        // 全部取り直しで、変わっていない予定がまた降ってきた
        _remote.FailNextWithGone = true;
        await Engine.SyncAsync("cal-a", "cal-a");

        Assert.False(Events.All().Single().GoogleMissing);
    }

    // ------------------------------------------------------------------
    // 未送信の編集が、Google 側の削除で警告なく消えない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 未送信の編集がある予定を相手が消したら警告を出す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Events.All().Single() with { Title = "棚卸し（まだ送っていない変更）" });
        _remote.Cancel("g1");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Events.All());
        Assert.Contains(report.Warnings, w =>
            w.Contains("Google 側で削除されたため、こちらの未送信の変更を捨てました", StringComparison.Ordinal) &&
            w.Contains("棚卸し（まだ送っていない変更）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 未送信の添付がある予定を相手が消したときも警告を出す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        Events.Upsert(Events.All().Single() with { PendingAttachments = "[]" });
        _remote.Cancel("g1");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Contains(report.Warnings, w => w.Contains("棚卸し", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 未送信の編集が無ければ相手が消しても警告を出さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.Cancel("g1");
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 繰り返しの回が中止されたときも未送信の編集があれば警告を出す()
    {
        _remote.AddRecurring("g1", "週次レビュー", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("g1_20261001", "g1", "2026-10-01", newDate: "2026-10-02", summary: "振替");
        await Engine.SyncAsync("cal-a", "cal-a");

        Events.Upsert(Events.All().Single(e => e.GoogleEventId == "g1_20261001") with { Title = "振替（まだ送っていない変更）" });
        _remote.AddException("g1_20261001", "g1", "2026-10-01", cancelled: true);

        var report = await Engine.SyncAsync("cal-a", "cal-a");

        Assert.DoesNotContain(Events.All(), e => e.GoogleEventId == "g1_20261001");
        Assert.Contains(report.Warnings, w => w.Contains("振替（まだ送っていない変更）", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 移す指示を出していて、送り終える前に切れた
    // ------------------------------------------------------------------

    private static JsonObject LiveAt(string id, string summary) => new()
    {
        ["id"] = id,
        ["summary"] = summary,
        ["status"] = "confirmed",
        ["updated"] = "2026-09-19T05:00:00.000Z",
        ["start"] = new JsonObject { ["date"] = "2026-09-24" },
        ["end"] = new JsonObject { ["date"] = "2026-09-25" },
    };

    [Fact]
    public async Task 移し先に確かにあれば元からのcancelledで消さず入れ先を直す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        // 入れ先を B へ変え、events.move は Google で通ったのに、応答を受け取る前に切れた。
        // 手元はまだ「A にいる」と思っている
        Events.Upsert(Events.All().Single() with { CalendarId = "cal-b", Title = "棚卸し（未送信の変更）" });
        _remote.Cancel("g1");
        _remote.Located[("cal-b", "g1")] = LiveAt("g1", "棚卸し");

        var report = await Engine.SyncAsync("cal-a", "cal-a");

        // 消さない。移動は済んでいたとして、Google での入れ先を直す
        Assert.Equal(0, report.DeletedLocal);
        var after = Assert.Single(Events.All());
        Assert.Equal("cal-b", after.GoogleCalendarId);
        Assert.Equal("cal-b", after.CalendarId);
        Assert.Equal("棚卸し（未送信の変更）", after.Title);
        Assert.Contains(("cal-b", "g1"), _remote.Got);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task 移し先にも無ければ消して未送信の編集は警告する()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        Events.Upsert(Events.All().Single() with { CalendarId = "cal-b", Title = "棚卸し（未送信の変更）" });
        _remote.Cancel("g1");

        var report = await Engine.SyncAsync("cal-a", "cal-a");

        // 本当に消された。移し先にも無い
        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Events.All());
        Assert.Contains(("cal-b", "g1"), _remote.Got);
        Assert.Contains(report.Warnings, w => w.Contains("棚卸し（未送信の変更）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 移す指示が無ければ移し先は確かめに行かない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("cal-a", "cal-a");

        _remote.Cancel("g1");
        await Engine.SyncAsync("cal-a", "cal-a");

        Assert.Empty(_remote.Got);
    }

    // ------------------------------------------------------------------
    // 結び付いていない同じものを探す範囲
    // ------------------------------------------------------------------

    [Fact]
    public async Task このアプリだけのカレンダーの同じ題と日付の予定は引き受けない()
    {
        // 仕事のカレンダーとは関係のない、私用の予定。たまたま同じ題・日付・開始時刻
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24), CalendarId = "local:shiyou",
            Note = "私用のメモ",
        });
        _remote.Add("g1", "棚卸し", "2026-09-24");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 吸い込んで上書きしない。どちらも別々に残る
        Assert.Equal(1, report.CreatedLocal);
        Assert.Equal(2, Events.All().Count);

        var untouched = Events.Find("e1")!;
        Assert.Null(untouched.GoogleEventId);
        Assert.Equal("local:shiyou", untouched.CalendarId);
        Assert.Equal("私用のメモ", untouched.Note);
    }

    [Fact]
    public async Task 別のカレンダーの未送信の新規も引き受けない()
    {
        // 別の Google カレンダーに入れる予定で、まだ送っていない
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24), CalendarId = "cal-b",
        });
        _remote.Add("g1", "棚卸し", "2026-09-24");

        await Engine.SyncAsync("cal-a", "cal-a");

        Assert.Equal(2, Events.All().Count);
        Assert.Equal("cal-b", Events.Find("e1")!.CalendarId);
        Assert.Null(Events.Find("e1")!.GoogleEventId);
    }
}
