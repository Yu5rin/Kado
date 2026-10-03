using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// 指定のカレンダーの予定だけを DB 側で絞って読む。
/// 全件を読んで絞る書き方（<c>All().Where(...)</c>）と、結果も並びも同じであること。
/// </summary>
public class EventByCalendarIdsTests
{
    private static CalendarEvent Make(string id, string? calendar, DateOnly date, TimeOnly? start = null) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = date,
        StartTime = start,
        EndTime = start?.AddHours(1),
        CalendarId = calendar,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static EventRepository Seed(TestDatabase db)
    {
        var repo = new EventRepository(db.Connection);
        var d = new DateOnly(2026, 9, 1);

        // 日付も時刻も入り乱れた順に入れる（並びを SQL の ORDER BY が決めることの確認）
        repo.Upsert(Make("a3", "cal-a", d.AddDays(5), new TimeOnly(9, 0)));
        repo.Upsert(Make("b1", "cal-b", d.AddDays(1)));
        repo.Upsert(Make("a1", "cal-a", d.AddDays(1), new TimeOnly(15, 0)));
        repo.Upsert(Make("c1", "cal-c", d.AddDays(2)));
        repo.Upsert(Make("a2", "cal-a", d.AddDays(1), new TimeOnly(8, 30)));
        repo.Upsert(Make("n1", null, d.AddDays(3)));
        repo.Upsert(Make("e1", "", d.AddDays(3)));
        repo.Upsert(Make("b2", "cal-b", d.AddDays(7)));
        return repo;
    }

    [Fact]
    public void 指定したカレンダーの予定だけが_全件を読んで絞ったときと同じ並びで返る()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        string[][] cases = [["cal-a"], ["cal-b"], ["cal-a", "cal-b"], ["cal-c", "cal-a", "cal-b"], ["cal-zzz"]];

        foreach (var ids in cases)
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            var expected = repo.All().Where(e => e.CalendarId is { } id && set.Contains(id)).ToArray();

            var actual = repo.ByCalendarIds(ids);

            Assert.Equal(expected.Select(e => e.Id), actual.Select(e => e.Id));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void 所属の無い予定と_指定していないカレンダーの予定は含めない()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        var result = repo.ByCalendarIds(["cal-a"]);

        Assert.Equal(["a2", "a1", "a3"], result.Select(e => e.Id));
        Assert.DoesNotContain(result, e => string.IsNullOrEmpty(e.CalendarId));
    }

    [Fact]
    public void 空の指定は空を返し_重複した指定でも1件ずつ返る()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        Assert.Empty(repo.ByCalendarIds([]));
        Assert.Equal(3, repo.ByCalendarIds(["cal-a", "cal-a", "cal-a"]).Count);
    }

    [Fact]
    public void カレンダー名の大文字小文字は区別する()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        // 全件を読んで ordinal で比べていた今までと同じ
        Assert.Empty(repo.ByCalendarIds(["CAL-A"]));
    }

    [Fact]
    public void 指定が多すぎるときも_全件から絞って同じ結果になる()
    {
        using var db = TestDatabase.Create();
        var repo = Seed(db);

        var ids = Enumerable.Range(0, 800).Select(i => $"x{i}").Append("cal-b").ToArray();

        Assert.Equal(["b1", "b2"], repo.ByCalendarIds(ids).Select(e => e.Id));
    }
}
