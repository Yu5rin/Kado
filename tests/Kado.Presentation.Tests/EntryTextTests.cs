using Kado.Data.Models;
using Kado.Presentation.Menus;

namespace Kado.Presentation.Tests;

/// <summary>
/// 「題名と日時をコピー」「題名をコピー」で置く1行の書き方。
/// 人に送る文面にそのまま貼れる形で、年は書かない。時刻には頼らない。
/// </summary>
public class EntryTextTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Timed() => new()
    {
        Id = "e1", Title = "打ち合わせ", Date = D(2026, 10, 6),
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
    };

    [Fact]
    public void 時刻付きの予定は日付と時刻の範囲と題名()
    {
        // 2026/10/6 は火曜日。仕様の例は「月」だが、曜日は日付から決まる
        Assert.Equal("10/6(火) 9:00–10:00 打ち合わせ", EntryText.ForEvent(Timed()));
    }

    [Fact]
    public void 月曜日の予定は月と書く()
    {
        Assert.Equal("10/5(月) 9:00–10:00 打ち合わせ", EntryText.ForEvent(Timed() with { Date = D(2026, 10, 5) }));
    }

    [Fact]
    public void 時刻は先頭の0を付けず_分は2桁にする()
    {
        var value = Timed() with { StartTime = new TimeOnly(9, 5), EndTime = new TimeOnly(13, 30) };

        Assert.Equal("10/6(火) 9:05–13:30 打ち合わせ", EntryText.ForEvent(value));
    }

    [Fact]
    public void 終日の予定は終日と書く()
    {
        var value = new CalendarEvent { Id = "e2", Title = "棚卸し", Date = D(2026, 10, 6) };

        Assert.Equal("10/6(火) 終日 棚卸し", EntryText.ForEvent(value));
    }

    [Fact]
    public void 複数日の予定は始まりと終わりの日付をつなぐ()
    {
        var value = new CalendarEvent { Id = "e3", Title = "出張", Date = D(2026, 10, 6), EndDate = D(2026, 10, 8) };

        Assert.Equal("10/6(火)–10/8(木) 出張", EntryText.ForEvent(value));
    }

    [Fact]
    public void 複数日で時刻付きなら両端に時刻を添える()
    {
        var value = new CalendarEvent
        {
            Id = "e3", Title = "出張", Date = D(2026, 10, 6), EndDate = D(2026, 10, 8),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0),
        };

        Assert.Equal("10/6(火) 9:00–10/8(木) 17:00 出張", EntryText.ForEvent(value));
    }

    [Fact]
    public void 場所があれば後ろに括弧で添える()
    {
        Assert.Equal(
            "10/6(火) 9:00–10:00 打ち合わせ（第2会議室）",
            EntryText.ForEvent(Timed() with { Location = "第2会議室" }));

        // 空の場所は書かない
        Assert.Equal("10/6(火) 9:00–10:00 打ち合わせ", EntryText.ForEvent(Timed() with { Location = "" }));
    }

    [Fact]
    public void 繰り返しの予定は右クリックした回の日付を書く()
    {
        var value = Timed() with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" };

        Assert.Equal("10/20(火) 9:00–10:00 打ち合わせ", EntryText.ForEvent(value, D(2026, 10, 20)));
    }

    [Fact]
    public void 繰り返さない予定は回の日付を渡されても元の日付を書く()
    {
        // 複数日の予定は、日ごとのチップのどれを右クリックしても、予定全体を指す
        var value = new CalendarEvent { Id = "e3", Title = "出張", Date = D(2026, 10, 6), EndDate = D(2026, 10, 8) };

        Assert.Equal("10/6(火)–10/8(木) 出張", EntryText.ForEvent(value, D(2026, 10, 7)));
    }

    [Fact]
    public void 同じ予定からは何度でも同じ文字になる()
    {
        Assert.Equal(EntryText.ForEvent(Timed()), EntryText.ForEvent(Timed()));
    }

    [Fact]
    public void タスクは期限があれば期限と題名_無ければ題名だけ()
    {
        Assert.Equal(
            "10/6(火) 期限 見積もり",
            EntryText.ForTask(new TaskItem { Id = "t1", Title = "見積もり", Due = D(2026, 10, 6) }));

        Assert.Equal("見積もり", EntryText.ForTask(new TaskItem { Id = "t2", Title = "見積もり" }));
    }
}
