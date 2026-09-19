using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Data.Tests;

/// <summary>
/// 保存された姿（繰り返しは開始日のみ、複数日は開始日と終了日）から、
/// カレンダーに並べる形へ開く処理。
/// </summary>
public class ScheduleQueryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (TestDatabase Db, ScheduleQuery Query, EventRepository Events, TaskRepository Tasks) Setup()
    {
        var db = TestDatabase.Create();
        var events = new EventRepository(db.Connection);
        var tasks = new TaskRepository(db.Connection);
        return (db, new ScheduleQuery(events, tasks), events, tasks);
    }

    private static CalendarEvent Event(string id, DateOnly date) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = date,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void 単発の予定はその日だけに現れる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        events.Upsert(Event("e1", D(2026, 9, 24)));

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        var single = Assert.Single(found);
        Assert.Equal(D(2026, 9, 24), single.Date);
        Assert.False(single.IsContinuation);
        Assert.False(single.IsRecurrence);
    }

    [Fact]
    public void 複数日の予定は日ごとに開かれる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        // 9/24 から 9/26 までの3日間
        events.Upsert(Event("multi", D(2026, 9, 24)) with { EndDate = D(2026, 9, 26) });

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Equal(3, found.Count);
        Assert.Equal([D(2026, 9, 24), D(2026, 9, 25), D(2026, 9, 26)], found.Select(e => e.Date));

        // 先頭の日だけ強調できるよう、2日目以降には印を付ける
        Assert.False(found[0].IsContinuation);
        Assert.True(found[1].IsContinuation);
        Assert.True(found[2].IsContinuation);
    }

    [Fact]
    public void 期間をまたぐ複数日予定は重なる分だけ開かれる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        // 8/30 から 9/2 まで続く予定を、9月だけ見たとき
        events.Upsert(Event("multi", D(2026, 8, 30)) with { EndDate = D(2026, 9, 2) });

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Equal([D(2026, 9, 1), D(2026, 9, 2)], found.Select(e => e.Date));
        // 期間の先頭に見えても、予定としては途中なので継続扱い
        Assert.All(found, e => Assert.True(e.IsContinuation));
    }

    [Fact]
    public void 繰り返し予定が展開される()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        // 2026/9/1 は火曜
        events.Upsert(Event("weekly", D(2026, 9, 1)) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Equal(
            [D(2026, 9, 1), D(2026, 9, 8), D(2026, 9, 15), D(2026, 9, 22), D(2026, 9, 29)],
            found.Select(e => e.Date));
        Assert.All(found, e => Assert.True(e.IsRecurrence));
    }

    [Fact]
    public void 繰り返しの除外日は展開されない()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        events.Upsert(Event("weekly", D(2026, 9, 1)) with
        {
            Recurrence = "FREQ=WEEKLY;BYDAY=TU;EXDATE=20260915",
        });

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        Assert.DoesNotContain(D(2026, 9, 15), found.Select(e => e.Date));
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public void 繰り返しの開始日より前には現れない()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        events.Upsert(Event("weekly", D(2026, 9, 15)) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Equal([D(2026, 9, 15), D(2026, 9, 22), D(2026, 9, 29)], found.Select(e => e.Date));
    }

    [Fact]
    public void 壊れた繰り返し指定でも他の予定は表示できる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        // 解釈できない指定。ここで例外を投げると、その月全体が表示できなくなる
        events.Upsert(Event("broken", D(2026, 9, 10)) with { Recurrence = "これは指定ではない" });
        events.Upsert(Event("normal", D(2026, 9, 24)));

        var found = query.EventsInRange(D(2026, 9, 1), D(2026, 9, 30));

        // 壊れたものは開始日に1回だけ出し、残りは通常どおり並ぶ
        Assert.Equal(2, found.Count);
        Assert.Contains(found, e => e.Source.Id == "broken" && e.Date == D(2026, 9, 10));
        Assert.Contains(found, e => e.Source.Id == "normal");
    }

    [Fact]
    public void 終日が先で時刻つきは開始時刻順に並ぶ()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        var day = D(2026, 9, 24);
        events.UpsertMany([
            Event("afternoon", day) with { StartTime = new TimeOnly(13, 30), EndTime = new TimeOnly(14, 0) },
            Event("allday", day),
            Event("morning", day) with { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) },
        ]);

        var found = query.EventsInRange(day, day);

        Assert.Equal(["allday", "morning", "afternoon"], found.Select(e => e.Source.Id));
    }

    [Fact]
    public void 日付ごとにまとめられる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        events.UpsertMany([
            Event("a", D(2026, 9, 24)),
            Event("b", D(2026, 9, 24)),
            Event("c", D(2026, 9, 25)),
        ]);

        var byDate = query.EventsByDate(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Equal(2, byDate[D(2026, 9, 24)].Count);
        Assert.Single(byDate[D(2026, 9, 25)]);
        Assert.False(byDate.ContainsKey(D(2026, 9, 26)));
    }

    [Fact]
    public void 期限つきタスクが日付ごとにまとまる()
    {
        var (db, query, _, tasks) = Setup();
        using var _db = db;

        tasks.UpsertMany([
            new TaskItem { Id = "t1", Title = "期限あり", Due = D(2026, 9, 24) },
            new TaskItem { Id = "t2", Title = "期限なし" },
        ]);

        var byDue = query.TasksByDue(D(2026, 9, 1), D(2026, 9, 30));

        Assert.Single(byDue);
        Assert.Single(byDue[D(2026, 9, 24)]);
    }

    [Fact]
    public void 期間の向きが逆でも同じ結果になる()
    {
        var (db, query, events, _) = Setup();
        using var _db = db;

        events.Upsert(Event("e1", D(2026, 9, 24)));

        Assert.Single(query.EventsInRange(D(2026, 9, 30), D(2026, 9, 1)));
    }
}
