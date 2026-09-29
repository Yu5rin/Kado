using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// タスクを「どの日に・どの見た目・どの添え書きで」出すかの決まり（<see cref="ScheduleQuery.TasksByDate"/>）。
/// <para>
/// 未完了は期限日。完了したものは完了した日（遅れて完了したものは期限日にも薄く）。
/// 月・週・日・一覧・右ペインは、ここで決まった結果を描くだけなので、決まりはここで固める。
/// </para>
/// </summary>
public class TaskPlacementTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>
    /// 2026年9月の実働日で数える書式。9/7〜9/11 は連続した実働日で、
    /// 9/12・9/13 は週末（非稼働日）。
    /// </summary>
    internal static DueDateFormatter Formatter(bool countInCalendarDays = false)
    {
        var days = Enumerable.Range(1, 30)
            .Select(d => D(2026, 9, d))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                        && d.Day is not (21 or 22 or 23))
            .ToArray();

        var calendar = WorkingDayCalendar.Create(days, D(2026, 9, 1), D(2026, 9, 30), null, null, null);
        return new DueDateFormatter(new WorkingDayMath(calendar), countInCalendarDays);
    }

    /// <summary>その日の昼（UTC）に完了した、という時刻。</summary>
    private static DateTimeOffset NoonUtc(int y, int m, int d) => new(y, m, d, 12, 0, 0, TimeSpan.Zero);

    private static TaskItem Task(string id, DateOnly? due, bool done = false, DateTimeOffset? completedAt = null) => new()
    {
        Id = id,
        Title = $"タスク{id}",
        Due = due,
        IsDone = done,
        CompletedAt = completedAt,
    };

    private static (TestDatabase Db, ScheduleQuery Query, TaskRepository Tasks) Setup(TimeZoneInfo? zone = null)
    {
        var db = TestDatabase.Create();
        var tasks = new TaskRepository(db.Connection);
        return (db, new ScheduleQuery(new EventRepository(db.Connection), tasks, zone ?? TimeZoneInfo.Utc), tasks);
    }

    private static IReadOnlyDictionary<DateOnly, IReadOnlyList<ScheduledTask>> Month(
        ScheduleQuery query, bool calendarDays = false) =>
        query.TasksByDate(D(2026, 9, 1), D(2026, 9, 30), Formatter(calendarDays));

    [Fact]
    public void 未完了は期限日に通常の見た目で出る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 24)));

        var placed = Assert.Single(Month(query)[D(2026, 9, 24)]);
        Assert.Equal(ScheduledTaskLook.Normal, placed.Look);
        Assert.Null(placed.Note);
        Assert.Equal("t1", placed.Id);
    }

    [Fact]
    public void 期限どおりに完了したものは完了した日にだけ出る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 9), done: true, NoonUtc(2026, 9, 9)));

        var byDate = Month(query);
        var placed = Assert.Single(Assert.Single(byDate).Value);
        Assert.Equal(D(2026, 9, 9), placed.Date);
        Assert.Equal(ScheduledTaskLook.Normal, placed.Look);
        Assert.Equal("期限どおり完了", placed.Note);
    }

    [Fact]
    public void 前倒しで完了したものは完了した日に出て期限日には出ない()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        // 期限は 9/10。9/8 に片付けた
        tasks.Upsert(Task("t1", D(2026, 9, 10), done: true, NoonUtc(2026, 9, 8)));

        var byDate = Month(query);
        Assert.False(byDate.ContainsKey(D(2026, 9, 10)));
        var placed = Assert.Single(byDate[D(2026, 9, 8)]);
        Assert.Equal(ScheduledTaskLook.Normal, placed.Look);
        Assert.Equal("2実働日 早く完了", placed.Note);
    }

    [Fact]
    public void 遅れて完了したものは完了日に期限と遅れを添えて出し期限日に薄く跡を残す()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        // 期限は 9/7。9/11 に片付けた。9/8〜9/11 の4実働日の遅れ
        tasks.Upsert(Task("t1", D(2026, 9, 7), done: true, NoonUtc(2026, 9, 11)));

        var byDate = Month(query);
        Assert.Equal(2, byDate.Count);

        var onDone = Assert.Single(byDate[D(2026, 9, 11)]);
        Assert.Equal(ScheduledTaskLook.Normal, onDone.Look);
        Assert.Equal("期限 9/7・4実働日遅れ", onDone.Note);

        var trace = Assert.Single(byDate[D(2026, 9, 7)]);
        Assert.Equal(ScheduledTaskLook.Faint, trace.Look);
        Assert.Equal("9/11 完了", trace.Note);
        Assert.Equal("9/11 完了", trace.FaintNote);
        Assert.Null(onDone.FaintNote);
    }

    [Fact]
    public void 遅れの日数は暦日で数える設定に従う()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 7), done: true, NoonUtc(2026, 9, 11)));

        // 暦日なら 9/7 → 9/11 は4日
        Assert.Equal("期限 9/7・4日遅れ", Month(query, calendarDays: true)[D(2026, 9, 11)].Single().Note);

        // 週末をまたぐと、実働日と暦日で数が変わる。9/9 → 9/14 は実働日で3、暦日で5
        tasks.Upsert(Task("t2", D(2026, 9, 9), done: true, NoonUtc(2026, 9, 14)));

        var working = Month(query, calendarDays: false)[D(2026, 9, 14)].Single(p => p.Id == "t2");
        var calendar = Month(query, calendarDays: true)[D(2026, 9, 14)].Single(p => p.Id == "t2");
        Assert.Equal("期限 9/9・3実働日遅れ", working.Note);
        Assert.Equal("期限 9/9・5日遅れ", calendar.Note);
    }

    [Fact]
    public void 完了日時が無い完了タスクは今までどおり期限日に出る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 9), done: true, completedAt: null));

        var placed = Assert.Single(Assert.Single(Month(query)).Value);
        Assert.Equal(D(2026, 9, 9), placed.Date);
        Assert.Equal(ScheduledTaskLook.Normal, placed.Look);
        Assert.True(placed.IsDone);
        Assert.Null(placed.Note);
    }

    [Fact]
    public void 期限の無い完了タスクは完了日に出る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", due: null, done: true, NoonUtc(2026, 9, 15)));

        var placed = Assert.Single(Assert.Single(Month(query)).Value);
        Assert.Equal(D(2026, 9, 15), placed.Date);
        Assert.Null(placed.Note);
    }

    [Fact]
    public void 期限も完了日時も無い完了タスクは出ない()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", due: null, done: true, completedAt: null));

        Assert.Empty(Month(query));
    }

    [Fact]
    public void 未完了に古い完了日時が残っていても期限日に出る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 20), done: false, NoonUtc(2026, 9, 3)));

        var byDate = Month(query);
        Assert.Equal([D(2026, 9, 20)], byDate.Keys);
    }

    [Fact]
    public void 完了した日はタイムゾーンをローカルに直して決める()
    {
        var jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
        var (db, query, tasks) = Setup(jst);
        using var _db = db;

        // UTC では 9/10 23:30 だが、日本時間では 9/11 8:30
        tasks.Upsert(Task("t1", due: null, done: true, new DateTimeOffset(2026, 9, 10, 23, 30, 0, TimeSpan.Zero)));

        // 9/11 だけを引いても、UTC 基準の 9/10 に隠れず拾える
        var byDate = query.TasksByDate(D(2026, 9, 11), D(2026, 9, 11), Formatter());
        Assert.Equal(D(2026, 9, 11), Assert.Single(byDate).Key);

        Assert.Empty(query.TasksByDate(D(2026, 9, 10), D(2026, 9, 10), Formatter()));
    }

    [Fact]
    public void 西回りのタイムゾーンでは前の日になる()
    {
        var hawaii = TimeZoneInfo.CreateCustomTimeZone("HST", TimeSpan.FromHours(-10), "HST", "HST");
        var (db, query, tasks) = Setup(hawaii);
        using var _db = db;

        // UTC では 9/11 5:00 だが、UTC-10 では 9/10 19:00
        tasks.Upsert(Task("t1", due: null, done: true, new DateTimeOffset(2026, 9, 11, 5, 0, 0, TimeSpan.Zero)));

        Assert.Equal(D(2026, 9, 10), Assert.Single(Month(query)).Key);
        Assert.Empty(query.TasksByDate(D(2026, 9, 11), D(2026, 9, 11), Formatter()));
    }

    [Fact]
    public void 期間の外で完了したものは出ず_期限日の跡だけが期間に入る()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        // 期限は 9/29 で、10/2 に完了。9月を引くと、期限日の跡だけが入る
        tasks.Upsert(Task("t1", D(2026, 9, 29), done: true, NoonUtc(2026, 10, 2)));

        var september = Month(query);
        var trace = Assert.Single(Assert.Single(september).Value);
        Assert.Equal(D(2026, 9, 29), trace.Date);
        Assert.Equal(ScheduledTaskLook.Faint, trace.Look);
        Assert.Equal("10/2 完了", trace.Note);

        // 10月を引くと、完了日の側だけが入る
        var october = query.TasksByDate(D(2026, 10, 1), D(2026, 10, 31), Formatter());
        var done = Assert.Single(Assert.Single(october).Value);
        Assert.Equal(D(2026, 10, 2), done.Date);
        Assert.Equal(ScheduledTaskLook.Normal, done.Look);
    }

    [Fact]
    public void 期限と完了日の年が違うときは年を添える()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 12, 28), done: true, NoonUtc(2027, 1, 4)));

        var december = query.TasksByDate(D(2026, 12, 1), D(2026, 12, 31), Formatter());
        Assert.Equal("2027/1/4 完了", december[D(2026, 12, 28)].Single().Note);

        var january = query.TasksByDate(D(2027, 1, 1), D(2027, 1, 31), Formatter());
        Assert.StartsWith("期限 2026/12/28・", january[D(2027, 1, 4)].Single().Note);
    }

    [Fact]
    public void 遅れて完了した日と期限日が同じ日なら重ねて出さない()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        // 期限は土曜（非稼働日）で、その土曜に完了。実働日では直前の金曜が期限になるので「遅れ」だが、
        // 同じマスに2つ出さない
        tasks.Upsert(Task("t1", D(2026, 9, 12), done: true, NoonUtc(2026, 9, 12)));

        Assert.Single(Assert.Single(Month(query)).Value);
    }

    [Fact]
    public void 同じ日の中では通常の見た目が先で_薄い跡は末尾()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        // 9/7 が期限で遅れて完了した跡（薄い）と、9/7 が期限の未完了
        tasks.UpsertMany([
            Task("a", D(2026, 9, 7), done: true, NoonUtc(2026, 9, 11)) with { SortOrder = 0 },
            Task("b", D(2026, 9, 7)) with { SortOrder = 1 },
        ]);

        Assert.Equal(["b", "a"], Month(query)[D(2026, 9, 7)].Select(p => p.Id));
    }

    [Fact]
    public void 期間の向きが逆でも同じ結果になる()
    {
        var (db, query, tasks) = Setup();
        using var _db = db;

        tasks.Upsert(Task("t1", D(2026, 9, 24)));

        Assert.Single(query.TasksByDate(D(2026, 9, 30), D(2026, 9, 1), Formatter()));
    }
}
