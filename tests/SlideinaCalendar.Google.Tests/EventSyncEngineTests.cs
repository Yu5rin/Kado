using System.Net;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Data;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Google.Sync;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// 予定の同期。
/// <para>
/// 順番は削除を伝える → 取り込む → 送る。削除を先に伝えないと、消したものが
/// 取り込みで復活する。
/// </para>
/// </summary>
public class EventSyncEngineTests : IDisposable
{
    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

    private readonly FakeEventGateway _remote = new();

    private EventRepository Events => new(_connection);

    private TombstoneRepository Tombstones => new(_connection);

    private SettingsRepository Settings => new(_connection);

    private EventSyncEngine Engine => new(Events, Tombstones, Settings, _remote);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task 相手にだけある予定を取り込む()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.CreatedLocal);

        var stored = Assert.Single(Events.All());
        Assert.Equal("棚卸し", stored.Title);
        Assert.Equal(D(2026, 9, 24), stored.Date);
        Assert.Equal("g1", stored.GoogleEventId);
        Assert.Equal("local:shigoto", stored.CalendarId);

        // 終日予定が1日伸びていない
        Assert.Null(stored.EndDate);
    }

    [Fact]
    public async Task こちらにだけある予定を送る()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "図面レビュー", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.CreatedRemote);
        Assert.Single(_remote.Items);

        // 送ったら結び付く。次から patch で送れる
        var stored = Assert.Single(Events.All());
        Assert.NotNull(stored.GoogleEventId);
    }

    [Fact]
    public async Task 二度目は何も起きない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");

        await Engine.SyncAsync("primary", "local:shigoto");
        var second = await Engine.SyncAsync("primary", "local:shigoto");

        // 毎回送ると更新時刻が動き、相手側でも「変わった」と見えてしまう
        Assert.Equal(0, second.CreatedRemote);
        Assert.Equal(0, second.UpdatedRemote);
        Assert.False(second.HasChanges);
    }

    [Fact]
    public async Task 送ったあとも予定は増えない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "図面レビュー", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        await Engine.SyncAsync("primary", "local:shigoto");
        await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Single(Events.All());
        Assert.Single(_remote.Items);
    }

    [Fact]
    public async Task 相手が消したらこちらも消す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.Cancel("g1");
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.DeletedLocal);
        Assert.Empty(Events.All());
    }

    [Fact]
    public async Task 相手が消したときは記録を残さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.Cancel("g1");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 相手はもう知っている。残すと次の同期で消しに行ってしまう
        Assert.Equal(0, Tombstones.Count());
    }

    [Fact]
    public async Task こちらで消したら相手でも消す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Tombstones.Record(stored.Id, TombstoneRepository.EventKind, "g1", DateTimeOffset.Now);
        Events.Delete(stored.Id);

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.DeletedRemote);
        Assert.Contains("g1", _remote.Deleted);

        // 伝え終わったら記録を消す
        Assert.Equal(0, Tombstones.Count());
    }

    [Fact]
    public async Task 消した予定は取り込みで復活しない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Tombstones.Record(stored.Id, TombstoneRepository.EventKind, "g1", DateTimeOffset.Now);
        Events.Delete(stored.Id);

        await Engine.SyncAsync("primary", "local:shigoto");

        // 削除を先に伝えているので、取り込みのときには相手からも消えている
        Assert.Empty(Events.All());
    }

    [Fact]
    public async Task 伝えられなかった削除は記録を残す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Tombstones.Record(stored.Id, TombstoneRepository.EventKind, "g1", DateTimeOffset.Now);
        Events.Delete(stored.Id);

        _remote.ThrowOnWrite = new GoogleApiException(HttpStatusCode.ServiceUnavailable, "backendError");
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 次の同期で出し直す
        Assert.Equal(1, Tombstones.Count());
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task すでに無いものの削除は成功として片付ける()
    {
        Tombstones.Record("e1", TombstoneRepository.EventKind, "kieta", DateTimeOffset.Now);

        _remote.ThrowOnWrite = new GoogleApiException(HttpStatusCode.NotFound, "notFound");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 消したいのだから、無いのは望む状態
        Assert.Equal(0, Tombstones.Count());
    }

    [Fact]
    public async Task 差分の印を覚えて次に使う()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.NextSyncToken = "token-2";
        await Engine.SyncAsync("primary", "local:shigoto");

        // 初回は印なし、2回目は覚えた印で呼ぶ
        Assert.Null(_remote.SeenSyncTokens[0]);
        Assert.Equal("token-1", _remote.SeenSyncTokens[1]);
    }

    [Fact]
    public async Task 印が古ければ全部取り直す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.FailNextWithGone = true;
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.True(report.FullResync);

        // 410 のあとは印なしで呼び直す
        Assert.Null(_remote.SeenSyncTokens[^1]);
    }

    [Fact]
    public async Task 結び付いていない同じ予定は引き受けて増やさない()
    {
        // 送ったあと応答を受け取る前に落ちた、という場面
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });
        _remote.Add("g1", "棚卸し", "2026-09-24");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 2件に増やさない
        var stored = Assert.Single(Events.All());
        Assert.Equal("e1", stored.Id);
        Assert.Equal("g1", stored.GoogleEventId);
        Assert.Equal(0, report.CreatedLocal);
    }

    [Fact]
    public async Task 相手から消えていたら結びを外して作り直す()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        // 相手の一覧には出ないまま、書き換えようとすると 404 になる状況
        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "棚卸し（変更）" });
        _remote.Items.Remove("g1");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.Relinked);
        Assert.Null(Events.All().Single().GoogleEventId);
    }

    [Fact]
    public async Task 変えた予定を送る()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "棚卸し（変更）", Location = "第2工場" });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.UpdatedRemote);
        Assert.Equal("棚卸し（変更）", _remote.Items["g1"]["summary"]!.GetValue<string>());
        Assert.Equal("第2工場", _remote.Items["g1"]["location"]!.GetValue<string>());
    }

    [Fact]
    public async Task 送ったあとは控えを更新して送り直さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "棚卸し（変更）" });

        await Engine.SyncAsync("primary", "local:shigoto");
        var third = await Engine.SyncAsync("primary", "local:shigoto");

        // 応答を控えていないと、毎回送り続ける
        Assert.Equal(0, third.UpdatedRemote);
    }

    [Fact]
    public async Task 他のカレンダーの予定は送らない()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "私用", Date = D(2026, 9, 24), CalendarId = "local:shiyou",
        });

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, report.CreatedRemote);
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 一時的な失敗では送信を諦めて知らせる()
    {
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "図面レビュー", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        _remote.ThrowOnWrite = new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded");
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(0, report.CreatedRemote);
        Assert.NotEmpty(report.Warnings);

        // 送れていないので、結び付いていないまま
        Assert.Null(Events.All().Single().GoogleEventId);
    }

    [Fact]
    public async Task 全部取り直しても送れていない変更を潰さない()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        // まだ送れていないこちらの変更
        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "棚卸し（変更）" });

        // 印が古くなり、相手の全件が降ってくる場面
        _remote.FailNextWithGone = true;
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.True(report.FullResync);

        // 相手は変わっていないのだから、こちらの変更を押し潰してはいけない
        Assert.Equal("棚卸し（変更）", Events.All().Single().Title);
        Assert.Equal("棚卸し（変更）", _remote.Items["g1"]["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task 相手が変わっていればこちらの変更より相手を採る()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        // どちらも変わった
        var stored = Assert.Single(Events.All());
        Events.Upsert(stored with { Title = "こちらの変更" });
        _remote.Edit("g1", "相手の変更");

        await Engine.SyncAsync("primary", "local:shigoto");

        // 予定は同僚からも書き換わる。こちらで放置した内容で押し戻すほうが困る
        Assert.Equal("相手の変更", Events.All().Single().Title);
    }

    [Fact]
    public async Task 相手の変更を取り込める()
    {
        _remote.Add("g1", "棚卸し", "2026-09-24");
        await Engine.SyncAsync("primary", "local:shigoto");

        _remote.Edit("g1", "棚卸し（相手で変更）");
        var report = await Engine.SyncAsync("primary", "local:shigoto");

        Assert.Equal(1, report.UpdatedLocal);
        Assert.Equal("棚卸し（相手で変更）", Events.All().Single().Title);
    }

    [Fact]
    public async Task 差し替えた回は親の繰り返しから除かれる()
    {
        // Google は繰り返しを親と例外回に分けて持つ。気づかず取り込むと二重に出る
        _remote.AddRecurring("g1", "週次レビュー", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("g1_20261001", "g1", "2026-10-01", newDate: "2026-10-02", summary: "週次レビュー（振替）");

        await Engine.SyncAsync("primary", "local:shigoto");

        var parent = Events.All().Single(e => e.GoogleEventId == "g1");
        Assert.Contains("EXDATE=20261001", parent.Recurrence);

        // 差し替えた回は独立した予定として残る
        var moved = Events.All().Single(e => e.GoogleEventId == "g1_20261001");
        Assert.Equal(D(2026, 10, 2), moved.Date);
        Assert.Equal("週次レビュー（振替）", moved.Title);
    }

    [Fact]
    public async Task 中止した回は親から除くだけで予定は作らない()
    {
        _remote.AddRecurring("g1", "週次レビュー", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("g1_20261001", "g1", "2026-10-01", cancelled: true);

        await Engine.SyncAsync("primary", "local:shigoto");

        var parent = Assert.Single(Events.All());
        Assert.Equal("g1", parent.GoogleEventId);
        Assert.Contains("EXDATE=20261001", parent.Recurrence);
    }

    [Fact]
    public async Task 例外回が先に降ってきても除外日が消えない()
    {
        _remote.AddRecurring("g1", "週次レビュー", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("g1_20261001", "g1", "2026-10-01", cancelled: true);

        // 相手が返す順番は決まっていない。親を先に処理しないと、あとで上書きされて消える
        _remote.PutFirst("g1_20261001");

        await Engine.SyncAsync("primary", "local:shigoto");

        var parent = Events.All().Single(e => e.GoogleEventId == "g1");
        Assert.Contains("EXDATE=20261001", parent.Recurrence);
    }

    [Fact]
    public async Task 何度同期しても除外日は増えない()
    {
        _remote.AddRecurring("g1", "週次レビュー", "2026-09-24", "FREQ=WEEKLY;BYDAY=TH");
        _remote.AddException("g1_20261001", "g1", "2026-10-01", cancelled: true);

        await Engine.SyncAsync("primary", "local:shigoto");
        await Engine.SyncAsync("primary", "local:shigoto");
        await Engine.SyncAsync("primary", "local:shigoto");

        var parent = Events.All().Single(e => e.GoogleEventId == "g1");

        // 同じ日を何度も足すと、指定がどんどん長くなる
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH;EXDATE=20261001", parent.Recurrence);
    }

    [Fact]
    public async Task 親を知らなければ何もしない()
    {
        // 差分で例外回だけが降ってきた場面。親は次に降りてくる
        _remote.AddException("g1_20261001", "g1", "2026-10-01", newDate: "2026-10-02");

        var report = await Engine.SyncAsync("primary", "local:shigoto");

        // 例外回そのものは取り込む。落とすと予定が消えたように見える
        Assert.Equal(1, report.CreatedLocal);
        Assert.Equal(D(2026, 10, 2), Events.All().Single().Date);
    }

    [Fact]
    public async Task 読むだけのカレンダーには送らない()
    {
        // 祝日や誕生日、他人から共有されたカレンダーは書けない。送れば断られる
        Events.Upsert(new CalendarEvent
        {
            Id = "e1", Title = "こちらで入れた予定", Date = D(2026, 9, 24), CalendarId = "local:shigoto",
        });

        var report = await Engine.SyncAsync("primary", "local:shigoto", readOnly: true);

        Assert.Equal(0, report.CreatedRemote);
        Assert.Empty(_remote.Items);
    }

    [Fact]
    public async Task 読むだけでも取り込みはする()
    {
        _remote.Add("g1", "海の日", "2026-07-20");

        var report = await Engine.SyncAsync("primary", "local:shukujitsu", readOnly: true);

        // 祝日カレンダーは読めないと意味がない
        Assert.Equal(1, report.CreatedLocal);
        Assert.Equal("海の日", Events.All().Single().Title);
    }

    [Fact]
    public async Task 読むだけなら削除も伝えない()
    {
        Tombstones.Record("e1", TombstoneRepository.EventKind, "g1", DateTimeOffset.Now);

        await Engine.SyncAsync("primary", "local:shukujitsu", readOnly: true);

        // 消せないカレンダーへ消しに行っても断られる。記録は残しておく
        Assert.Empty(_remote.Deleted);
        Assert.Equal(1, Tombstones.Count());
    }

    [Fact]
    public async Task 終日予定は往復しても日付が動かない()
    {
        // 同期のたびに1日ずつ伸びる不具合を防ぐ
        _remote.Add("g1", "連休の作業", "2026-09-21", endDate: "2026-09-24");

        await Engine.SyncAsync("primary", "local:shigoto");
        await Engine.SyncAsync("primary", "local:shigoto");
        await Engine.SyncAsync("primary", "local:shigoto");

        var stored = Assert.Single(Events.All());
        Assert.Equal(D(2026, 9, 21), stored.Date);
        Assert.Equal(D(2026, 9, 23), stored.EndDate);

        // 相手側も動いていない
        Assert.Equal("2026-09-24", _remote.Items["g1"]["end"]!["date"]!.GetValue<string>());
    }
}
