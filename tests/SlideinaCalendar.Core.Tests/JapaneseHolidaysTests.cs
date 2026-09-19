using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

/// <summary>
/// 日本の祝日。
/// <para>
/// 実機の画面と突き合わせられる年を選んである。2026年9月は、敬老の日・国民の休日・
/// 秋分の日が3日続く並びで、ここが合っていれば計算はおおむね正しい。
/// </para>
/// </summary>
public class JapaneseHolidaysTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(2026, 1, 1, "元日")]
    [InlineData(2026, 1, 12, "成人の日")]        // 1月第2月曜
    [InlineData(2026, 2, 11, "建国記念の日")]
    [InlineData(2026, 2, 23, "天皇誕生日")]
    [InlineData(2026, 3, 20, "春分の日")]
    [InlineData(2026, 4, 29, "昭和の日")]
    [InlineData(2026, 5, 3, "憲法記念日")]
    [InlineData(2026, 5, 4, "みどりの日")]
    [InlineData(2026, 5, 5, "こどもの日")]
    [InlineData(2026, 5, 6, "振替休日")]         // 5月3日が日曜
    [InlineData(2026, 7, 20, "海の日")]          // 7月第3月曜
    [InlineData(2026, 8, 11, "山の日")]
    [InlineData(2026, 9, 21, "敬老の日")]        // 9月第3月曜
    [InlineData(2026, 9, 22, "国民の休日")]      // 敬老の日と秋分の日に挟まれる
    [InlineData(2026, 9, 23, "秋分の日")]
    [InlineData(2026, 10, 12, "スポーツの日")]   // 10月第2月曜
    [InlineData(2026, 11, 3, "文化の日")]
    [InlineData(2026, 11, 23, "勤労感謝の日")]
    public void 二千二十六年の祝日(int y, int m, int d, string name)
    {
        Assert.Equal(name, JapaneseHolidays.NameOf(D(y, m, d)));
    }

    [Theory]
    [InlineData(2026, 9, 24)]
    [InlineData(2026, 12, 23)]   // 2019年以降は天皇誕生日ではない
    [InlineData(2026, 12, 31)]
    public void 祝日でない日は返さない(int y, int m, int d)
    {
        Assert.Null(JapaneseHolidays.NameOf(D(y, m, d)));
    }

    [Theory]
    [InlineData(2020, 7, 23, "海の日")]          // 五輪に合わせて動いた年
    [InlineData(2020, 7, 24, "スポーツの日")]
    [InlineData(2020, 8, 10, "山の日")]
    [InlineData(2021, 7, 22, "海の日")]
    [InlineData(2021, 7, 23, "スポーツの日")]
    [InlineData(2021, 8, 8, "山の日")]
    [InlineData(2019, 5, 1, "天皇の即位の日")]
    [InlineData(2019, 10, 22, "即位礼正殿の儀の行われる日")]
    public void その年だけ動いた祝日(int y, int m, int d, string name)
    {
        Assert.Equal(name, JapaneseHolidays.NameOf(D(y, m, d)));
    }

    [Fact]
    public void 五輪の年は元の日付に置かない()
    {
        // 2020年の海の日は7月第3月曜（20日）ではない
        Assert.Null(JapaneseHolidays.NameOf(D(2020, 7, 20)));
        Assert.Null(JapaneseHolidays.NameOf(D(2020, 8, 11)));
    }

    [Fact]
    public void 天皇誕生日は二千十九年で切り替わる()
    {
        Assert.Equal("天皇誕生日", JapaneseHolidays.NameOf(D(2018, 12, 23)));
        Assert.Equal("天皇誕生日", JapaneseHolidays.NameOf(D(2020, 2, 23)));

        // 2019年はどちらでもない
        Assert.Null(JapaneseHolidays.NameOf(D(2019, 12, 23)));
        Assert.Null(JapaneseHolidays.NameOf(D(2019, 2, 23)));
    }

    [Fact]
    public void 振替休日は次の祝日でない日に置く()
    {
        // 2021年8月8日（日）が山の日。9日（月）が振替休日
        Assert.Equal("振替休日", JapaneseHolidays.NameOf(D(2021, 8, 9)));

        // 2023年1月1日（日）の振替は2日
        Assert.Equal("振替休日", JapaneseHolidays.NameOf(D(2023, 1, 2)));
    }

    [Fact]
    public void 扱える年の外では何も返さない()
    {
        Assert.Null(JapaneseHolidays.NameOf(D(1999, 1, 1)));
        Assert.Null(JapaneseHolidays.NameOf(D(2100, 1, 1)));

        Assert.Empty(JapaneseHolidays.Of(1999));
        Assert.Empty(JapaneseHolidays.Of(2100));
    }

    [Fact]
    public void 一年分をまとめて返せる()
    {
        var days = JapaneseHolidays.Of(2026);

        // 16日＋振替休日＋国民の休日
        Assert.Equal(18, days.Count);
        Assert.All(days, pair => Assert.Equal(2026, pair.Key.Year));
    }
}
