using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

/// <summary>
/// 済んだタスクが期限に間に合ったか。
/// <para>
/// Google ToDo は期限と完了日時の両方を持つので、その差から出せる。
/// メモに書き足す必要はない。
/// </para>
/// </summary>
public class DoneTextTests
{
    private static readonly DueDateFormatter Sut = new(TestCalendars.September2026);

    private static DateOnly D(int day) => new(2026, 9, day);

    [Fact]
    public void 期限の日に済ませたら期限どおり()
    {
        var done = Sut.FormatDone(due: D(24), completed: D(24));

        Assert.Equal("期限どおり完了", done.Text);
        Assert.Equal(DoneKind.OnTime, done.Kind);
    }

    [Fact]
    public void 遅れた分を実働日で数える()
    {
        // 9/24（木）期限を 9/28（月）に済ませた。あいだの土日は数えない
        var done = Sut.FormatDone(due: D(24), completed: D(28));

        Assert.Equal(DoneKind.Late, done.Kind);
        Assert.Equal("2実働日 遅れて完了", done.Text);
        Assert.False(done.IsCalendarUnit);
    }

    [Fact]
    public void 早く終えた分も実働日で数える()
    {
        // 9/24（木）期限を 9/18（金）に済ませた。あいだの 9/19〜23 は
        // 土日と3連休なので、実働では1日ぶんしか早くない
        var done = Sut.FormatDone(due: D(24), completed: D(18));

        Assert.Equal(DoneKind.Early, done.Kind);
        Assert.Equal("1実働日 早く完了", done.Text);
    }

    [Fact]
    public void 期限が休みの日なら直前の実働日に寄せて数える()
    {
        // 9/23（水・秋分の日）は休み。実働では 9/18（金）が最後
        var done = Sut.FormatDone(due: D(23), completed: D(18));

        Assert.Equal(DoneKind.OnTime, done.Kind);
    }

    [Fact]
    public void 実働日データの外は暦日で数える()
    {
        var done = Sut.FormatDone(due: new DateOnly(2027, 3, 31), completed: new DateOnly(2027, 4, 2));

        Assert.Equal(DoneKind.Late, done.Kind);
        Assert.Equal("2日 遅れて完了", done.Text);
        Assert.True(done.IsCalendarUnit);
    }

    [Fact]
    public void 休みの日に済ませた分は実働日で数える()
    {
        // 9/24 期限を 9/26（土）に済ませた。あいだの実働日は 9/25 だけ
        var done = Sut.FormatDone(due: D(24), completed: D(26));

        Assert.Equal("1実働日 遅れて完了", done.Text);
        Assert.False(done.IsCalendarUnit);
    }

    [Fact]
    public void 暦日で数える設定なら休みも数える()
    {
        var calendarDays = new DueDateFormatter(TestCalendars.September2026Math, countInCalendarDays: true);

        // 9/24 期限を 9/28 に済ませた。実働日なら2日だが、暦では4日
        var done = calendarDays.FormatDone(due: D(24), completed: D(28));

        Assert.Equal("4日 遅れて完了", done.Text);
        Assert.True(done.IsCalendarUnit);
    }

    [Fact]
    public void 暦日で数える設定なら期限も暦日で出す()
    {
        var calendarDays = new DueDateFormatter(TestCalendars.September2026Math, countInCalendarDays: true);

        // 9/18 から見た 9/24。実働日なら1日、暦では6日
        var due = calendarDays.Format(due: D(24), today: D(18));

        Assert.Equal("残り 6日", due.Text);
        Assert.Equal(DueKind.CalendarDays, due.Kind);
    }
}
