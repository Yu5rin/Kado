using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

/// <summary>
/// 期限表示の組み立て。要件書 4.4 の表と、非稼働日への丸めを網羅する。
/// </summary>
public class DueDateFormatterTests
{
    private static readonly DueDateFormatter Sut = TestCalendars.September2026Formatter;

    private static DateOnly Sep(int day) => new(2026, 9, day);

    // ------------------------------------------------------------------
    // 期限が今日
    // ------------------------------------------------------------------

    [Fact]
    public void 期限が今日なら今日まで()
    {
        var result = Sut.Format(due: Sep(24), today: Sep(24));

        Assert.Equal("今日まで", result.Text);
        Assert.Equal(DueKind.Today, result.Kind);
        Assert.Equal(0, result.Amount);
        Assert.False(result.IsSnapped);
    }

    [Fact]
    public void 今日が非稼働日でも期限が今日なら今日まで()
    {
        var result = Sut.Format(due: Sep(23), today: Sep(23));   // 秋分の日
        Assert.Equal("今日まで", result.Text);
        Assert.Equal(DueKind.Today, result.Kind);
    }

    [Fact]
    public void データ範囲外でも期限が今日なら今日まで()
    {
        var day = new DateOnly(2027, 4, 1);
        Assert.Equal("今日まで", Sut.Format(due: day, today: day).Text);
    }

    // ------------------------------------------------------------------
    // 実働日で数えられる範囲
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(24, 25, 1)]   // 翌実働日
    [InlineData(18, 24, 1)]   // 三連休をまたぐ
    [InlineData(4, 7, 1)]     // 土日をまたぐ
    [InlineData(1, 30, 18)]   // 月初から月末
    [InlineData(23, 25, 2)]   // 今日が非稼働日（秋分の日）
    public void 残りを実働日で数える(int today, int due, int expected)
    {
        var result = Sut.Format(due: Sep(due), today: Sep(today));

        Assert.Equal($"残り {expected}実働日", result.Text);
        Assert.Equal(DueKind.WorkingDays, result.Kind);
        Assert.Equal(expected, result.Amount);
        Assert.False(result.IsCalendarUnit);
        Assert.False(result.IsSnapped);
        Assert.Equal(Sep(due), result.EffectiveDue);
    }

    [Fact]
    public void 今日は数えず期限日は数える()
    {
        // 9/24 と 9/25 はどちらも実働日。間に何も無いので「残り 1実働日」
        Assert.Equal("残り 1実働日", Sut.Format(due: Sep(25), today: Sep(24)).Text);
    }

    // ------------------------------------------------------------------
    // 期限日そのものが非稼働日 → 直前の実働日に寄せ、寄せた結果を明示する
    // ------------------------------------------------------------------

    [Fact]
    public void 要件書の例_残り2実働日_9月25日まで()
    {
        // 今日 9/23（秋分の日）、期限 9/27（日）。9/27 は非稼働なので直前の実働日 9/25 に寄せる。
        // (9/23, 9/25] の実働日は 24・25 の 2 日。
        var result = Sut.Format(due: Sep(27), today: Sep(23));

        Assert.Equal("残り 2実働日（9/25まで）", result.Text);
        Assert.Equal(DueKind.WorkingDays, result.Kind);
        Assert.Equal(2, result.Amount);
        Assert.True(result.IsSnapped);
        Assert.Equal(Sep(25), result.EffectiveDue);
    }

    [Fact]
    public void 期限が土曜なら直前の金曜に寄せる()
    {
        var result = Sut.Format(due: Sep(26), today: Sep(24));

        Assert.Equal("残り 1実働日（9/25まで）", result.Text);
        Assert.True(result.IsSnapped);
        Assert.Equal(Sep(25), result.EffectiveDue);
    }

    [Fact]
    public void 寄せた結果が今日になったら今日まで()
    {
        // 期限 9/27（日）は 9/25 に寄る。今日がその 9/25 なら、実働日では今日が最後。
        var result = Sut.Format(due: Sep(27), today: Sep(25));

        Assert.Equal("今日まで", result.Text);
        Assert.Equal(DueKind.Today, result.Kind);
        Assert.True(result.IsSnapped);
        Assert.Equal(Sep(25), result.EffectiveDue);
    }

    [Fact]
    public void 寄せ先が登録範囲に無ければ暦日に倒す()
    {
        // 範囲は 9/5 からだが最初の実働日は 9/7。9/5・9/6 は寄せ先が無い。
        var calendar = WorkingDayCalendar.Create(
            TestCalendars.September2026WorkingDays.Where(d => d.Day >= 7),
            new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 30),
            null, null, null);
        var formatter = new DueDateFormatter(calendar);

        var result = formatter.Format(due: Sep(6), today: Sep(5));

        Assert.Equal("残り 1日", result.Text);
        Assert.Equal(DueKind.CalendarDays, result.Kind);
    }

    // ------------------------------------------------------------------
    // 超過
    // ------------------------------------------------------------------

    [Fact]
    public void 要件書の例_3実働日遅れ()
    {
        // 今日 9/28、期限 9/18。(9/18, 9/28] の実働日は 24・25・28 の 3 日。
        var result = Sut.Format(due: Sep(18), today: Sep(28));

        Assert.Equal("3実働日 遅れ", result.Text);
        Assert.Equal(DueKind.Overdue, result.Kind);
        Assert.Equal(3, result.Amount);
        Assert.False(result.IsCalendarUnit);
    }

    [Fact]
    public void 一日過ぎたら1実働日遅れ()
        => Assert.Equal("1実働日 遅れ", Sut.Format(due: Sep(24), today: Sep(25)).Text);

    [Fact]
    public void 超過していて期限が非稼働日なら寄せ先も明示する()
    {
        // 期限 9/19（土）→ 9/18 に寄る。(9/18, 9/25] は 24・25 の 2 日。
        var result = Sut.Format(due: Sep(19), today: Sep(25));

        Assert.Equal("2実働日 遅れ（9/18まで）", result.Text);
        Assert.Equal(DueKind.Overdue, result.Kind);
        Assert.True(result.IsSnapped);
    }

    [Fact]
    public void 実働日換算で0日の超過は暦日で表す()
    {
        // 今日 9/26（土）、期限 9/27（日）→ 9/25 に寄る。
        // (9/25, 9/26] に実働日は無いので「0実働日 遅れ」になってしまう。ここだけ暦日に倒す。
        var result = Sut.Format(due: Sep(27), today: Sep(26));

        Assert.Equal("1日 遅れ（9/25まで）", result.Text);
        Assert.Equal(DueKind.Overdue, result.Kind);
        Assert.True(result.IsCalendarUnit);
        Assert.Equal(1, result.Amount);
    }

    // ------------------------------------------------------------------
    // 暦日フォールバック
    // ------------------------------------------------------------------

    [Fact]
    public void 要件書の例_残り189日()
    {
        // 期限が翌年度で実働日データの範囲外。単位を落として暦日で表す。
        var result = Sut.Format(due: new DateOnly(2027, 4, 1), today: Sep(24));

        Assert.Equal("残り 189日", result.Text);
        Assert.Equal(DueKind.CalendarDays, result.Kind);
        Assert.Equal(189, result.Amount);
        Assert.True(result.IsCalendarUnit);
        Assert.False(result.IsSnapped);
    }

    [Fact]
    public void 期限が範囲外なら暦日に倒す()
    {
        var result = Sut.Format(due: new DateOnly(2026, 10, 1), today: Sep(24));

        Assert.Equal("残り 7日", result.Text);
        Assert.Equal(DueKind.CalendarDays, result.Kind);
    }

    [Fact]
    public void 今日が範囲外なら暦日に倒す()
    {
        var result = Sut.Format(due: Sep(10), today: new DateOnly(2026, 8, 31));

        Assert.Equal("残り 10日", result.Text);
        Assert.Equal(DueKind.CalendarDays, result.Kind);
    }

    [Fact]
    public void 範囲外の超過は暦日で表す()
    {
        var result = Sut.Format(due: Sep(30), today: new DateOnly(2026, 10, 5));

        Assert.Equal("5日 遅れ", result.Text);
        Assert.Equal(DueKind.Overdue, result.Kind);
        Assert.True(result.IsCalendarUnit);
    }

    [Fact]
    public void 実働日データが空なら常に暦日()
    {
        var formatter = new DueDateFormatter(WorkingDayCalendar.Empty);

        Assert.Equal("残り 3日", formatter.Format(due: Sep(4), today: Sep(1)).Text);
        Assert.Equal("3日 遅れ", formatter.Format(due: Sep(1), today: Sep(4)).Text);
        Assert.Equal("今日まで", formatter.Format(due: Sep(1), today: Sep(1)).Text);
    }

    // ------------------------------------------------------------------
    // 種別の一貫性
    // ------------------------------------------------------------------

    [Fact]
    public void 過去の期限は必ずOverdueになる()
    {
        // 実働日でも暦日でも、寄せが入っても、過去なら Overdue で揃っていること
        foreach (var due in Enumerable.Range(1, 23).Select(Sep))
        {
            var result = Sut.Format(due, today: Sep(24));
            Assert.Equal(DueKind.Overdue, result.Kind);
        }
    }

    [Fact]
    public void 未来の期限はOverdueにならない()
    {
        // 9/25 以降で「9/24 より後の実働日がある」期限は残りとして扱われる
        foreach (var due in Enumerable.Range(25, 6).Select(Sep))
        {
            var result = Sut.Format(due, today: Sep(24));
            Assert.NotEqual(DueKind.Overdue, result.Kind);
        }
    }
}
