using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

public class WorkingDayMathTests
{
    private static readonly WorkingDayMath Sut = TestCalendars.September2026Math;

    private static DateOnly Sep(int day) => new(2026, 9, day);

    // ------------------------------------------------------------------
    // CountBetween: from は数えず to は数える
    // ------------------------------------------------------------------

    [Fact]
    public void 同じ日どうしは0()
        => Assert.Equal(0, Sut.CountBetween(Sep(1), Sep(1)));

    [Fact]
    public void 翌稼働日までは1()
        => Assert.Equal(1, Sut.CountBetween(Sep(1), Sep(2)));

    [Fact]
    public void 土日をまたいでも実働日だけを数える()
        => Assert.Equal(1, Sut.CountBetween(Sep(4), Sep(7)));   // 5・6 は土日

    [Fact]
    public void 三連休をまたいでも実働日だけを数える()
        => Assert.Equal(1, Sut.CountBetween(Sep(18), Sep(24))); // 19〜23 は休み

    [Fact]
    public void 月初から月末までは開始日を除いた実働日数()
        => Assert.Equal(18, Sut.CountBetween(Sep(1), Sep(30))); // 全19日 − 開始日

    [Fact]
    public void 非稼働日を起点にしても数えられる()
    {
        // (9/23, 9/25] → 24・25 の 2 日。要件書 4.4 の例と同じ並び
        Assert.Equal(2, Sut.CountBetween(Sep(23), Sep(25)));
    }

    [Fact]
    public void 期間の向きが逆なら負の値を返す()
    {
        Assert.Equal(-1, Sut.CountBetween(Sep(2), Sep(1)));
        Assert.Equal(-18, Sut.CountBetween(Sep(30), Sep(1)));
    }

    [Theory]
    [InlineData(2026, 8, 31, 2026, 9, 10)]   // 開始が範囲外
    [InlineData(2026, 9, 1, 2026, 10, 1)]    // 終了が範囲外
    [InlineData(2025, 1, 1, 2027, 1, 1)]     // 両端とも範囲外
    public void 端がデータ範囲外ならnull(int y1, int m1, int d1, int y2, int m2, int d2)
        => Assert.Null(Sut.CountBetween(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2)));

    // ------------------------------------------------------------------
    // AddWorkingDays
    // ------------------------------------------------------------------

    [Fact]
    public void ゼロ実働日後は基準日そのもの()
    {
        Assert.Equal(Sep(1), Sut.AddWorkingDays(Sep(1), 0));
        Assert.Equal(Sep(19), Sut.AddWorkingDays(Sep(19), 0));  // 非稼働日でも自身を返す
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(4, 1, 7)]     // 金 → 翌月曜
    [InlineData(18, 1, 24)]   // 三連休を飛ばす
    [InlineData(1, 18, 30)]   // 月末まで
    [InlineData(19, 1, 24)]   // 土曜を基準にしても直後の実働日を返す
    public void N実働日後を求める(int from, int n, int expected)
        => Assert.Equal(Sep(expected), Sut.AddWorkingDays(Sep(from), n));

    [Theory]
    [InlineData(30, -1, 29)]
    [InlineData(24, -1, 18)]  // 三連休を飛ばす
    [InlineData(19, -1, 18)]  // 土曜を基準にしても直前の実働日を返す
    [InlineData(30, -18, 1)]
    public void N実働日前を求める(int from, int n, int expected)
        => Assert.Equal(Sep(expected), Sut.AddWorkingDays(Sep(from), n));

    [Fact]
    public void 到達不能ならnull()
    {
        Assert.Null(Sut.AddWorkingDays(Sep(1), 19));    // 9月には 19 日しか実働日が無い
        Assert.Null(Sut.AddWorkingDays(Sep(1), -1));
        Assert.Null(Sut.AddWorkingDays(Sep(30), 1));
    }

    [Fact]
    public void 基準日がデータ範囲外ならnull()
        => Assert.Null(Sut.AddWorkingDays(new DateOnly(2026, 8, 31), 1));

    [Fact]
    public void CountBetweenとAddWorkingDaysは互いの逆になる()
    {
        // 境界規則（from は数えず to は数える）が両者で揃っていることの確認
        foreach (var n in Enumerable.Range(1, 18))
        {
            var reached = Sut.AddWorkingDays(Sep(1), n);
            Assert.NotNull(reached);
            Assert.Equal(n, Sut.CountBetween(Sep(1), reached.Value));
        }
    }

    // ------------------------------------------------------------------
    // 前後の実働日
    // ------------------------------------------------------------------

    [Fact]
    public void 直前直後の実働日を求める()
    {
        Assert.Equal(Sep(18), Sut.PreviousWorkingDay(Sep(24)));
        Assert.Equal(Sep(24), Sut.NextWorkingDay(Sep(18)));
    }

    [Fact]
    public void 直前直後は基準日自身を含まない()
    {
        Assert.Equal(Sep(1), Sut.PreviousWorkingDay(Sep(2)));
        Assert.Equal(Sep(2), Sut.NextWorkingDay(Sep(1)));
    }

    [Fact]
    public void 自身を含む版は稼働日ならその日を返す()
    {
        Assert.Equal(Sep(18), Sut.PreviousWorkingDayOrSame(Sep(18)));
        Assert.Equal(Sep(18), Sut.NextWorkingDayOrSame(Sep(18)));
    }

    [Fact]
    public void 自身を含む版は非稼働日なら寄せる()
    {
        Assert.Equal(Sep(18), Sut.PreviousWorkingDayOrSame(Sep(19)));  // 土 → 直前の金
        Assert.Equal(Sep(24), Sut.NextWorkingDayOrSame(Sep(19)));      // 土 → 連休明け
        Assert.Equal(Sep(18), Sut.PreviousWorkingDayOrSame(Sep(23)));  // 秋分の日 → 直前の金
    }

    [Fact]
    public void 範囲外ならnullを返す()
    {
        Assert.Null(Sut.PreviousWorkingDayOrSame(new DateOnly(2026, 10, 1)));
        Assert.Null(Sut.NextWorkingDayOrSame(new DateOnly(2026, 8, 31)));
    }

    // ------------------------------------------------------------------
    // 月をまたぐ計算
    // ------------------------------------------------------------------

    [Fact]
    public void 月をまたいでも数えられる()
    {
        var math = TestCalendars.MathFor(TestCalendars.AutumnToYearEnd2026);

        // 9/30 の翌実働日は 10/1
        Assert.Equal(new DateOnly(2026, 10, 1), math.NextWorkingDay(new DateOnly(2026, 9, 30)));
        Assert.Equal(1, math.CountBetween(new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void カレンダーが空なら全てnull()
    {
        var empty = new WorkingDayMath(WorkingDayCalendar.Empty);
        Assert.Null(empty.CountBetween(Sep(1), Sep(2)));
        Assert.Null(empty.AddWorkingDays(Sep(1), 1));
        Assert.Null(empty.PreviousWorkingDay(Sep(1)));
    }
}
