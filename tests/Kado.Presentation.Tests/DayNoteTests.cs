using Kado.Core.WorkingDays;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 日付に添える一言。休業日・特別出勤を色だけでなく文字でも出す
/// （取り込んでも何も表示されない、と受け取られたため）。
/// </summary>
public class DayNoteTests
{
    // 2027年7月。7/19 は海の日（月）。7/16（金）・7/21（水）は休み、7/24（土）は出勤とする
    private static WorkingDayCalendar July2027()
    {
        var from = new DateOnly(2027, 7, 1);
        var to = new DateOnly(2027, 7, 31);
        var days = new List<DateOnly>();

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (!weekend && d.Day is not (16 or 19 or 21)) days.Add(d);
        }

        days.Add(new DateOnly(2027, 7, 24));
        return WorkingDayCalendar.Create(days, from, to, [], null, null);
    }

    [Fact]
    public void 平日の休みは休業日()
    {
        var note = DayNote.Of(new DateOnly(2027, 7, 16), null, July2027());

        Assert.Equal("休業日", note.Text);
        Assert.False(note.IsOpenDay);
    }

    [Fact]
    public void 休みの祝日は祝日名だけ()
    {
        Assert.Equal("海の日", DayNote.Of(new DateOnly(2027, 7, 19), "海の日", July2027()).Text);
    }

    [Fact]
    public void 土曜の出勤は特別出勤()
    {
        var note = DayNote.Of(new DateOnly(2027, 7, 24), null, July2027());

        Assert.Equal("特別出勤", note.Text);
        Assert.True(note.IsOpenDay);
    }

    [Fact]
    public void 祝日の出勤は祝日名に特別出勤を添える()
    {
        var calendar = WorkingDayCalendar.Create(
            [new DateOnly(2027, 7, 19)], new DateOnly(2027, 7, 1), new DateOnly(2027, 7, 31), [], null, null);

        var note = DayNote.Of(new DateOnly(2027, 7, 19), "海の日", calendar);

        Assert.Equal("海の日・特別出勤", note.Text);
        Assert.True(note.IsOpenDay);
    }

    [Fact]
    public void ふつうの稼働日と土日の休みには何も出さない()
    {
        Assert.Null(DayNote.Of(new DateOnly(2027, 7, 20), null, July2027()).Text);
        Assert.Null(DayNote.Of(new DateOnly(2027, 7, 25), null, July2027()).Text);
    }

    [Fact]
    public void データの範囲外は休みかどうか分からないので祝日名だけ()
    {
        Assert.Null(DayNote.Of(new DateOnly(2027, 8, 4), null, July2027()).Text);
        Assert.Equal("山の日", DayNote.Of(new DateOnly(2027, 8, 11), "山の日", July2027()).Text);
    }
}
