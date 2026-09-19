using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

public class TimeInputTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("09", 9, 0)]
    [InlineData("13", 13, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("1330", 13, 30)]
    [InlineData("9:30", 9, 30)]
    [InlineData("09:30", 9, 30)]
    [InlineData("9:5", 9, 5)]
    [InlineData(" 9 : 30 ", 9, 30)]
    public void いろいろな打ち方を読む(string text, int hour, int minute)
    {
        // コロンと 0 埋めを毎回求められるのは、1日に何度も触る画面では煩わしい
        Assert.Equal(T(hour, minute), TimeInput.Parse(text));
    }

    [Theory]
    [InlineData("９：３０", 9, 30)]
    [InlineData("９30", 9, 30)]
    public void 全角でも読む(string text, int hour, int minute)
    {
        Assert.Equal(T(hour, minute), TimeInput.Parse(text));
    }

    [Fact]
    public void 候補から選んだ表示もそのまま読む()
    {
        // 候補は「10:30（1時間30分）」の形で出す。添え字を消してから渡す必要をなくす
        Assert.Equal(T(10, 30), TimeInput.Parse("10:30（1時間30分）"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("きゅうじ")]
    [InlineData("25:00")]
    [InlineData("9:70")]
    [InlineData("99999")]
    public void 読めないものはnull(string? text)
    {
        Assert.Null(TimeInput.Parse(text));
    }

    [Theory]
    [InlineData(9, 0, 10, 30, "1時間30分")]
    [InlineData(9, 0, 10, 0, "1時間")]
    [InlineData(9, 0, 9, 30, "30分")]
    public void 長さを文にする(int fh, int fm, int th, int tm, string expected)
    {
        Assert.Equal(expected, TimeInput.FormatDuration(T(fh, fm), T(th, tm)));
    }

    [Fact]
    public void 長さが0以下ならnull()
    {
        Assert.Null(TimeInput.FormatDuration(T(10), T(10)));
        Assert.Null(TimeInput.FormatDuration(T(10), T(9)));
    }

    [Fact]
    public void 候補は15分刻みで1日分()
    {
        var choices = TimeInput.EveryQuarterHour();

        Assert.Equal(96, choices.Count);
        Assert.Equal("00:00", choices[0]);
        Assert.Equal("00:15", choices[1]);
        Assert.Equal("23:45", choices[^1]);
    }
}
