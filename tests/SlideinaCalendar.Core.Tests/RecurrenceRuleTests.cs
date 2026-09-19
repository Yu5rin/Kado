using SlideinaCalendar.Core.Recurrence;

namespace SlideinaCalendar.Core.Tests;

public class RecurrenceRuleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>2026/9/1 は火曜、9/2 は水曜。曜日の確認に使う基準日。</summary>
    private static readonly DateOnly Tuesday = D(2026, 9, 1);

    // ------------------------------------------------------------------
    // 毎日
    // ------------------------------------------------------------------

    [Fact]
    public void 毎日は開始日以降の全ての日に該当する()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY");

        Assert.True(rule.Matches(Tuesday, Tuesday));            // 開始日そのもの
        Assert.True(rule.Matches(D(2026, 9, 2), Tuesday));
        Assert.True(rule.Matches(D(2027, 1, 1), Tuesday));
        Assert.False(rule.Matches(D(2026, 8, 31), Tuesday));    // 開始日より前
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    public void 毎日は間隔を指定できる(int day, bool expected)
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;INTERVAL=3");
        Assert.Equal(expected, rule.Matches(D(2026, 9, day), Tuesday));
    }

    // ------------------------------------------------------------------
    // 毎週（曜日指定）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]    // 火
    [InlineData(8, true)]
    [InlineData(15, true)]
    [InlineData(29, true)]
    [InlineData(2, false)]   // 水
    [InlineData(7, false)]   // 月
    public void 毎週は指定曜日に該当する(int day, bool expected)
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU");
        Assert.Equal(expected, rule.Matches(D(2026, 9, day), Tuesday));
    }

    [Fact]
    public void 曜日を省略したら開始日の曜日を使う()
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY");

        Assert.True(rule.Matches(D(2026, 9, 8), Tuesday));    // 火
        Assert.False(rule.Matches(D(2026, 9, 9), Tuesday));   // 水
    }

    [Fact]
    public void 複数曜日を指定できる()
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=MO,WE,FR");
        var start = D(2026, 9, 2);   // 水

        Assert.True(rule.Matches(D(2026, 9, 2), start));    // 水
        Assert.True(rule.Matches(D(2026, 9, 4), start));    // 金
        Assert.True(rule.Matches(D(2026, 9, 7), start));    // 月
        Assert.False(rule.Matches(D(2026, 9, 3), start));   // 木
    }

    [Theory]
    [InlineData(2, true)]     // 開始週の水
    [InlineData(4, true)]     // 開始週の金
    [InlineData(7, false)]    // 翌週は飛ばす
    [InlineData(9, false)]
    [InlineData(14, true)]    // 2週後の月
    [InlineData(16, true)]    // 2週後の水
    public void 隔週は週単位で飛ばす(int day, bool expected)
    {
        // 週の起点は月曜（RFC 5545 の既定 WKST=MO）
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR");
        Assert.Equal(expected, rule.Matches(D(2026, 9, day), D(2026, 9, 2)));
    }

    // ------------------------------------------------------------------
    // 毎月（日付指定）
    // ------------------------------------------------------------------

    [Fact]
    public void 毎月は指定日に該当する()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY;BYMONTHDAY=15");
        var start = D(2026, 1, 15);

        Assert.True(rule.Matches(D(2026, 1, 15), start));
        Assert.True(rule.Matches(D(2026, 2, 15), start));
        Assert.True(rule.Matches(D(2026, 12, 15), start));
        Assert.False(rule.Matches(D(2026, 2, 14), start));
    }

    [Fact]
    public void 日付を省略したら開始日の日を使う()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY");
        var start = D(2026, 3, 10);

        Assert.True(rule.Matches(D(2026, 4, 10), start));
        Assert.False(rule.Matches(D(2026, 4, 11), start));
    }

    [Fact]
    public void 三十一日指定はその日が無い月をとばす()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY");
        var start = D(2026, 1, 31);

        Assert.True(rule.Matches(D(2026, 1, 31), start));
        Assert.False(rule.Matches(D(2026, 2, 28), start));   // 2月に 31 日は無い
        Assert.True(rule.Matches(D(2026, 3, 31), start));
    }

    [Fact]
    public void 月末はマイナス1で指定する()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY;BYMONTHDAY=-1");
        var start = D(2026, 1, 31);

        Assert.True(rule.Matches(D(2026, 1, 31), start));
        Assert.True(rule.Matches(D(2026, 2, 28), start));
        Assert.True(rule.Matches(D(2026, 4, 30), start));
        Assert.False(rule.Matches(D(2026, 4, 29), start));
    }

    [Fact]
    public void 隔月は月単位で飛ばす()
    {
        var rule = RecurrenceRule.Parse("FREQ=MONTHLY;INTERVAL=2;BYMONTHDAY=1");
        var start = D(2026, 1, 1);

        Assert.True(rule.Matches(D(2026, 3, 1), start));
        Assert.False(rule.Matches(D(2026, 2, 1), start));
    }

    // ------------------------------------------------------------------
    // 毎年
    // ------------------------------------------------------------------

    [Fact]
    public void 毎年は同じ月日に該当する()
    {
        var rule = RecurrenceRule.Parse("FREQ=YEARLY");
        var start = D(2026, 9, 19);

        Assert.True(rule.Matches(D(2027, 9, 19), start));
        Assert.True(rule.Matches(D(2030, 9, 19), start));
        Assert.False(rule.Matches(D(2027, 9, 20), start));
        Assert.False(rule.Matches(D(2027, 10, 19), start));
    }

    [Fact]
    public void 毎年は月日を明示できる()
    {
        var rule = RecurrenceRule.Parse("FREQ=YEARLY;BYMONTH=4;BYMONTHDAY=1");
        var start = D(2026, 9, 19);

        Assert.True(rule.Matches(D(2027, 4, 1), start));
        Assert.False(rule.Matches(D(2027, 9, 19), start));
    }

    // ------------------------------------------------------------------
    // 終了日（UNTIL）
    // ------------------------------------------------------------------

    [Fact]
    public void 終了日を過ぎたら該当しない()
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261006");

        Assert.Equal(D(2026, 10, 6), rule.Until);
        Assert.True(rule.Matches(D(2026, 9, 29), Tuesday));
        Assert.True(rule.Matches(D(2026, 10, 6), Tuesday));    // 終了日そのものは含む
        Assert.False(rule.Matches(D(2026, 10, 13), Tuesday));
    }

    [Fact]
    public void 終了日は日時形式でもハイフン区切りでも読める()
    {
        Assert.Equal(D(2026, 12, 31), RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20261231T145959Z").Until);
        Assert.Equal(D(2026, 12, 31), RecurrenceRule.Parse("FREQ=DAILY;UNTIL=2026-12-31").Until);
    }

    // ------------------------------------------------------------------
    // 除外日（EXDATE）
    // ------------------------------------------------------------------

    [Fact]
    public void 除外日は該当しない()
    {
        // 旧 inaCalendar のバックアップに、毎年の予定から特定の年だけ外した例があった
        var rule = RecurrenceRule.Parse("FREQ=YEARLY;EXDATE=20260421,20270421");
        var start = D(2026, 4, 21);

        Assert.False(rule.Matches(D(2026, 4, 21), start));   // 開始日でも除外されていれば該当しない
        Assert.False(rule.Matches(D(2027, 4, 21), start));
        Assert.True(rule.Matches(D(2028, 4, 21), start));    // 除外されていない年は該当する
    }

    [Fact]
    public void 除外日を指定しなければ空になる()
        => Assert.Empty(RecurrenceRule.Parse("FREQ=DAILY").ExceptDates);

    [Fact]
    public void 除外日は列挙からも外れる()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;EXDATE=20260903");
        var days = rule.Occurrences(D(2026, 9, 1), D(2026, 9, 1), D(2026, 9, 4)).ToArray();

        Assert.Equal([D(2026, 9, 1), D(2026, 9, 2), D(2026, 9, 4)], days);
    }

    [Fact]
    public void 除外日は終了日と併用できる()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20260904;EXDATE=20260903");

        Assert.Equal(D(2026, 9, 4), rule.Until);
        Assert.Single(rule.ExceptDates);
        Assert.False(rule.Matches(D(2026, 9, 3), D(2026, 9, 1)));
        Assert.True(rule.Matches(D(2026, 9, 4), D(2026, 9, 1)));
        Assert.False(rule.Matches(D(2026, 9, 5), D(2026, 9, 1)));
    }

    [Fact]
    public void 除外日はパターンから組み立てても効く()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY");
        var withExcept = RecurrenceRule.FromPattern(rule.Pattern, exceptDates: [D(2026, 9, 3)]);

        Assert.False(withExcept.Matches(D(2026, 9, 3), D(2026, 9, 1)));
        Assert.True(withExcept.Matches(D(2026, 9, 2), D(2026, 9, 1)));
    }

    // ------------------------------------------------------------------
    // 列挙
    // ------------------------------------------------------------------

    [Fact]
    public void 期間内の該当日を列挙できる()
    {
        var rule = RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU");
        var days = rule.Occurrences(Tuesday, D(2026, 9, 1), D(2026, 9, 30)).ToArray();

        Assert.Equal([D(2026, 9, 1), D(2026, 9, 8), D(2026, 9, 15), D(2026, 9, 22), D(2026, 9, 29)], days);
    }

    [Fact]
    public void 列挙は開始日より前と終了日より後を含まない()
    {
        var rule = RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20260903");
        var days = rule.Occurrences(D(2026, 9, 2), D(2026, 8, 1), D(2026, 12, 31)).ToArray();

        Assert.Equal([D(2026, 9, 2), D(2026, 9, 3)], days);
    }

    // ------------------------------------------------------------------
    // 表示用ラベル
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("FREQ=DAILY", "毎日")]
    [InlineData("FREQ=DAILY;INTERVAL=3", "3日ごと")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU", "毎週 火曜")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR", "毎週 月曜・水曜・金曜")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU", "2週ごと 火曜")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15", "毎月 15日")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1", "毎月 月末")]
    [InlineData("FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=1", "3か月ごと 1日")]
    [InlineData("FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=19", "毎年 9月19日")]
    public void 表示用ラベルを組み立てる(string spec, string expected)
        => Assert.Equal(expected, RecurrenceRule.Parse(spec).ToLabel());

    [Fact]
    public void 省略した指定でも開始日を渡せば具体的に表示できる()
    {
        Assert.Equal("毎週 火曜", RecurrenceRule.Parse("FREQ=WEEKLY").ToLabel(Tuesday));
        Assert.Equal("毎月 19日", RecurrenceRule.Parse("FREQ=MONTHLY").ToLabel(D(2026, 9, 19)));
        Assert.Equal("毎年 9月19日", RecurrenceRule.Parse("FREQ=YEARLY").ToLabel(D(2026, 9, 19)));
    }

    [Fact]
    public void 終了日つきはラベルに括弧で添える()
        => Assert.Equal("毎週 火曜（2026/12/31まで）",
            RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231").ToLabel());

    [Fact]
    public void 曜日は日曜始まりの順に揃える()
        => Assert.Equal("毎週 月曜・水曜・金曜", RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=FR,MO,WE").ToLabel());

    // ------------------------------------------------------------------
    // 文字列への復元
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=DAILY;INTERVAL=2")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15")]
    [InlineData("FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=19")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231")]
    [InlineData("FREQ=YEARLY;EXDATE=20260421,20270421")]
    [InlineData("FREQ=DAILY;UNTIL=20261231;EXDATE=20260903")]
    public void 指定文字列に復元できる(string spec)
        => Assert.Equal(spec, RecurrenceRule.Parse(spec).ToSpec());

    // ------------------------------------------------------------------
    // 入力の揺れと異常系
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("freq=weekly;byday=tu")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TU")]     // Google Calendar の形式
    [InlineData(" FREQ=WEEKLY ; BYDAY=TU ")]
    public void 大文字小文字と余分な空白を受け入れる(string spec)
        => Assert.True(RecurrenceRule.Parse(spec).Matches(D(2026, 9, 8), Tuesday));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INTERVAL=2")]                   // FREQ が無い
    [InlineData("FREQ=HOURLY")]                  // 未対応の種別
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]         // 曜日が不正
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]    // 0 は不正
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=32")]
    [InlineData("FREQ=YEARLY;BYMONTH=13")]
    [InlineData("FREQ=DAILY;UNTIL=abc")]
    [InlineData("FREQ=DAILY;EXDATE=abc")]
    [InlineData("FREQ")]                         // 値が無い
    public void 不正な指定はTryParseで弾ける(string spec)
    {
        Assert.False(RecurrenceRule.TryParse(spec, out var rule));
        Assert.Null(rule);
    }

    [Fact]
    public void 不正な指定はParseで例外になる()
    {
        Assert.Throws<FormatException>(() => RecurrenceRule.Parse("FREQ=HOURLY"));
        Assert.Throws<ArgumentException>(() => RecurrenceRule.Parse("  "));
    }

    [Fact]
    public void 間隔に不正な値が来たら1として扱う()
    {
        // 表示や判定を壊さないよう、既定値に倒す
        Assert.Equal(1, RecurrenceRule.Parse("FREQ=DAILY;INTERVAL=0").Interval);
        Assert.Equal(1, RecurrenceRule.Parse("FREQ=DAILY;INTERVAL=-5").Interval);
    }

    // ------------------------------------------------------------------
    // 拡張
    // ------------------------------------------------------------------

    [Fact]
    public void 種別は後から追加できる()
    {
        // 要件書 11 章のとおり対応種別は未確定なので、既存に手を入れず足せることを保証する
        RecurrenceRule.RegisterPattern("EVERY-OTHER-DAY-TEST", _ => new EveryOtherDayPattern());

        var rule = RecurrenceRule.Parse("FREQ=EVERY-OTHER-DAY-TEST");

        Assert.True(rule.Matches(D(2026, 9, 3), Tuesday));
        Assert.False(rule.Matches(D(2026, 9, 2), Tuesday));
        Assert.Equal("1日おき（テスト用）", rule.ToLabel());
        Assert.Contains("EVERY-OTHER-DAY-TEST", RecurrenceRule.SupportedFrequencies);
    }

    private sealed class EveryOtherDayPattern : IRecurrencePattern
    {
        public string Frequency => "EVERY-OTHER-DAY-TEST";
        public int Interval => 2;

        public bool Matches(DateOnly date, DateOnly seriesStart)
            => (date.DayNumber - seriesStart.DayNumber) % 2 == 0;

        public string ToLabel(DateOnly? seriesStart) => "1日おき（テスト用）";
        public string ToSpec() => "FREQ=EVERY-OTHER-DAY-TEST";
    }
}
