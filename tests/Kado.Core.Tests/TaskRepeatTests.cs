using Kado.Core.Recurrence;
using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

/// <summary>
/// タスクの繰り返し（Kado 独自）。指定文字列の読み書きと、次の回の期限。
/// <para>
/// 時計には頼らない。「今日」は引数で渡す。2025/10/6 は月曜、10/13 は月曜（スポーツの日）。
/// </para>
/// </summary>
public class TaskRepeatTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>土日だけを休みとする判定（祝日を絡めたくないテスト用）。</summary>
    private static bool Weekday(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    // ------------------------------------------------------------------
    // 指定文字列を読む・書く
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("FREQ=DAILY", TaskRepeatKind.Daily)]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", TaskRepeatKind.Weekly)]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", TaskRepeatKind.Monthly)]
    [InlineData("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29", TaskRepeatKind.Yearly)]
    [InlineData("X-KADO=WEEK-FIRST-WORKDAY", TaskRepeatKind.WeekFirstWorkday)]
    [InlineData("X-KADO=WEEK-LAST-WORKDAY", TaskRepeatKind.WeekLastWorkday)]
    [InlineData("X-KADO=MONTH-FIRST-WORKDAY", TaskRepeatKind.MonthFirstWorkday)]
    [InlineData("X-KADO=MONTH-LAST-WORKDAY", TaskRepeatKind.MonthLastWorkday)]
    public void 八つの指定を読める(string spec, TaskRepeatKind kind)
    {
        Assert.Equal(kind, TaskRepeat.Parse(spec)!.Value.Kind);
    }

    [Fact]
    public void 並び順と大文字小文字が違っても読める()
    {
        var rule = TaskRepeat.Parse(" freq=yearly ; bymonthday=29 ; bymonth=2 ")!.Value;

        Assert.Equal(TaskRepeatKind.Yearly, rule.Kind);
        Assert.Equal(2, rule.Month);
        Assert.Equal(29, rule.DayOfMonth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("こわれた文字列")]
    [InlineData("FREQ=")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=DAILY;INTERVAL=2")]             // 間隔の指定は画面で作れない
    [InlineData("FREQ=WEEKLY")]                       // 曜日を明示していない
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("FREQ=MONTHLY")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=32")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=0")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30")]   // どの年にも無い日
    [InlineData("FREQ=YEARLY;BYMONTH=13;BYMONTHDAY=1")]
    [InlineData("FREQ=YEARLY;BYMONTHDAY=1")]
    [InlineData("X-KADO=SOMETIME")]
    [InlineData("X-KADO=WEEK-FIRST-WORKDAY;FREQ=DAILY")]
    [InlineData("FREQ=DAILY;FREQ=DAILY")]
    public void 読めない指定は繰り返さないとして扱う(string? spec)
    {
        Assert.Null(TaskRepeat.Parse(spec));
        Assert.Null(TaskRepeat.NextDue(spec, D(2025, 10, 6), D(2025, 10, 6)));
    }

    [Theory]
    [InlineData(TaskRepeatKind.Daily, "FREQ=DAILY")]
    [InlineData(TaskRepeatKind.Weekly, "FREQ=WEEKLY;BYDAY=MO")]      // 2025/10/6 は月曜
    [InlineData(TaskRepeatKind.Monthly, "FREQ=MONTHLY;BYMONTHDAY=6")]
    [InlineData(TaskRepeatKind.Yearly, "FREQ=YEARLY;BYMONTH=10;BYMONTHDAY=6")]
    [InlineData(TaskRepeatKind.WeekFirstWorkday, "X-KADO=WEEK-FIRST-WORKDAY")]
    [InlineData(TaskRepeatKind.WeekLastWorkday, "X-KADO=WEEK-LAST-WORKDAY")]
    [InlineData(TaskRepeatKind.MonthFirstWorkday, "X-KADO=MONTH-FIRST-WORKDAY")]
    [InlineData(TaskRepeatKind.MonthLastWorkday, "X-KADO=MONTH-LAST-WORKDAY")]
    public void 暦どおりの4つは期限日の曜日と日付を明示して書く(TaskRepeatKind kind, string expected)
    {
        var spec = TaskRepeat.ToSpec(kind, D(2025, 10, 6));

        Assert.Equal(expected, spec);

        // 書いたものは読み戻せる
        Assert.Equal(kind, TaskRepeat.Parse(spec)!.Value.Kind);
    }

    [Fact]
    public void 期限日が変わると暦どおりの指定は指定し直し稼働日基準と読めない指定はそのまま()
    {
        // 月曜 → 水曜
        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", TaskRepeat.Respec("FREQ=WEEKLY;BYDAY=MO", D(2025, 10, 8)));
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=8", TaskRepeat.Respec("FREQ=MONTHLY;BYMONTHDAY=31", D(2025, 10, 8)));
        Assert.Equal("FREQ=YEARLY;BYMONTH=10;BYMONTHDAY=8",
            TaskRepeat.Respec("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29", D(2025, 10, 8)));

        Assert.Equal("X-KADO=MONTH-LAST-WORKDAY", TaskRepeat.Respec("X-KADO=MONTH-LAST-WORKDAY", D(2025, 10, 8)));

        // 読めない指定は消さない
        Assert.Equal("こわれた", TaskRepeat.Respec("こわれた", D(2025, 10, 8)));
        Assert.Null(TaskRepeat.Respec(null, D(2025, 10, 8)));
    }

    // ------------------------------------------------------------------
    // 毎日・毎週
    // ------------------------------------------------------------------

    [Fact]
    public void 毎日は期限の翌日と今日の遅いほう()
    {
        const string spec = "FREQ=DAILY";

        Assert.Equal(D(2025, 10, 7), TaskRepeat.NextDue(spec, D(2025, 10, 6), today: D(2025, 10, 3)));   // 前倒し
        Assert.Equal(D(2025, 10, 7), TaskRepeat.NextDue(spec, D(2025, 10, 6), today: D(2025, 10, 6)));   // 期限の日に完了
        Assert.Equal(D(2025, 10, 20), TaskRepeat.NextDue(spec, D(2025, 10, 6), today: D(2025, 10, 20))); // 溜めた：今日以降
    }

    [Theory]
    [InlineData(2025, 10, 3, 2025, 10, 13)]   // 金曜に完了（前倒し）。同じ週の月曜は今の期限なので次の週
    [InlineData(2025, 10, 6, 2025, 10, 13)]   // 期限の日に完了
    [InlineData(2025, 10, 8, 2025, 10, 13)]   // 水曜に完了
    [InlineData(2025, 10, 13, 2025, 10, 13)]  // 1週間ためて、次の月曜に完了。今日が該当日なら今日（今日以降）
    [InlineData(2025, 10, 20, 2025, 10, 20)]
    [InlineData(2025, 10, 30, 2025, 11, 3)]   // 2週間以上ためてから完了。今日以降の最初の月曜
    public void 毎週月曜は期限より後で今日以降のいちばん早い月曜(int y, int m, int d, int ey, int em, int ed)
    {
        var next = TaskRepeat.NextDue("FREQ=WEEKLY;BYDAY=MO", D(2025, 10, 6), today: D(y, m, d));

        Assert.Equal(D(ey, em, ed), next);
    }

    [Fact]
    public void 毎週は次の回の期限の日に完了したら翌週()
    {
        // 次の回（期限 10/13）を、その日に完了 → 10/20
        Assert.Equal(D(2025, 10, 20), TaskRepeat.NextDue("FREQ=WEEKLY;BYDAY=MO", D(2025, 10, 13), today: D(2025, 10, 13)));
    }

    // ------------------------------------------------------------------
    // 毎月・毎年（無い日は月末に寄せ、寄せても元の日付に戻る）
    // ------------------------------------------------------------------

    [Fact]
    public void 毎月31日は短い月で月末に寄せて次の月は31日に戻る()
    {
        const string spec = "FREQ=MONTHLY;BYMONTHDAY=31";

        var due = D(2026, 1, 31);
        var chain = new List<DateOnly>();

        // 毎回、期限の日に完了した体で5回たどる。指定は書き換えずに持ち越す
        for (var i = 0; i < 5; i++)
        {
            due = TaskRepeat.NextDue(spec, due, today: due)!.Value;
            chain.Add(due);
        }

        Assert.Equal(
            [D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30), D(2026, 5, 31), D(2026, 6, 30)],
            chain);
    }

    [Fact]
    public void 毎月31日の2月は閏年なら29日()
    {
        Assert.Equal(D(2028, 2, 29), TaskRepeat.NextDue("FREQ=MONTHLY;BYMONTHDAY=31", D(2028, 1, 31), D(2028, 1, 31)));
    }

    [Fact]
    public void 毎月は溜めて完了したら今日以降の最初の該当日()
    {
        // 期限は10/15。12/20 に完了 → 12/15 は過ぎているので翌月の 1/15
        Assert.Equal(D(2026, 1, 15), TaskRepeat.NextDue("FREQ=MONTHLY;BYMONTHDAY=15", D(2025, 10, 15), D(2025, 12, 20)));

        // 今日の日付が該当日ならその日
        Assert.Equal(D(2025, 12, 15), TaskRepeat.NextDue("FREQ=MONTHLY;BYMONTHDAY=15", D(2025, 10, 15), D(2025, 12, 15)));
    }

    [Fact]
    public void 毎月は前倒しで完了しても今の期限より後の月()
    {
        // 10/15 期限を 10/1 に完了。同じ月の 10/15 は今の期限そのものなので 11/15
        Assert.Equal(D(2025, 11, 15), TaskRepeat.NextDue("FREQ=MONTHLY;BYMONTHDAY=15", D(2025, 10, 15), D(2025, 10, 1)));
    }

    [Fact]
    public void 毎年2月29日は無い年は2月28日に寄せ閏年に戻る()
    {
        const string spec = "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29";

        var due = D(2028, 2, 29);
        var chain = new List<DateOnly>();

        for (var i = 0; i < 4; i++)
        {
            due = TaskRepeat.NextDue(spec, due, today: due)!.Value;
            chain.Add(due);
        }

        Assert.Equal(
            [D(2029, 2, 28), D(2030, 2, 28), D(2031, 2, 28), D(2032, 2, 29)],
            chain);
    }

    [Fact]
    public void 毎年は溜めて完了したら今日以降の最初の該当日()
    {
        const string spec = "FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=19";

        // 期限 2025/9/19 を 2027/1/5 に完了 → 2027/9/19
        Assert.Equal(D(2027, 9, 19), TaskRepeat.NextDue(spec, D(2025, 9, 19), D(2027, 1, 5)));

        // 期限の日に完了 → 来年
        Assert.Equal(D(2026, 9, 19), TaskRepeat.NextDue(spec, D(2025, 9, 19), D(2025, 9, 19)));
    }

    // ------------------------------------------------------------------
    // 稼働日基準（週）
    // ------------------------------------------------------------------

    [Fact]
    public void 週始めは次の週の最初の稼働日で月曜が休みなら火曜()
    {
        // 2025/10/13(月) はスポーツの日。10/6(月) 期限を 10/6 に完了 → 次の週の最初の稼働日は 10/14(火)
        Assert.NotNull(JapaneseHolidays.NameOf(D(2025, 10, 13)));

        var next = TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 6));

        Assert.Equal(D(2025, 10, 14), next);
    }

    [Fact]
    public void 週始めは同じ週の最初の稼働日が期限より前なら次の週へ進む()
    {
        // 期限 10/6(月)。水曜 10/8 に完了しても、10/6 は過去なので次の週
        Assert.Equal(D(2025, 10, 14),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 8)));
    }

    [Fact]
    public void 週始めは今の期限より後で今日以降のうち最初の週の最初の稼働日()
    {
        // 期限が前の週の火曜(10/7)で、前倒しで 10/3(金)に完了。lower=10/8(水)。
        // 同じ週(10/5〜)の最初の稼働日 10/6 は過ぎているので 10/14
        Assert.Equal(D(2025, 10, 14),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 7), D(2025, 10, 3)));

        // 2週間ためて 10/30(木)に完了 → 次は 11/3 の週。11/3 は文化の日なので 11/4(火)
        Assert.Equal(D(2025, 11, 4),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 30)));
    }

    [Fact]
    public void 週終わりはその週の最後の稼働日で期限より後ならその週のうちに来る()
    {
        // 期限 10/8(水)。同じ週の最後の稼働日 10/10(金) は期限より後
        Assert.Equal(D(2025, 10, 10),
            TaskRepeat.NextDue("X-KADO=WEEK-LAST-WORKDAY", D(2025, 10, 8), D(2025, 10, 8), Weekday));

        // 期限が金曜(10/10)なら次の週の金曜
        Assert.Equal(D(2025, 10, 17),
            TaskRepeat.NextDue("X-KADO=WEEK-LAST-WORKDAY", D(2025, 10, 10), D(2025, 10, 10), Weekday));
    }

    [Fact]
    public void 週終わりは金曜が休みなら木曜()
    {
        bool FridayOff(DateOnly date) => Weekday(date) && date != D(2025, 10, 17);

        Assert.Equal(D(2025, 10, 16),
            TaskRepeat.NextDue("X-KADO=WEEK-LAST-WORKDAY", D(2025, 10, 10), D(2025, 10, 10), FridayOff));
    }

    [Fact]
    public void 週の区切りは週の始まりの設定に合わせる()
    {
        // 期限 10/8(水)。日曜始まりなら次の週の最初の稼働日は 10/13(月)、水曜始まりの週は 10/15(水)から
        Assert.Equal(D(2025, 10, 13),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 8), D(2025, 10, 8), Weekday, DayOfWeek.Sunday));
        Assert.Equal(D(2025, 10, 15),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 8), D(2025, 10, 8), Weekday, DayOfWeek.Wednesday));

        // 水曜始まりなら週の最後の稼働日は火曜（10/8 の週は 10/8〜10/14 → 10/14）
        Assert.Equal(D(2025, 10, 14),
            TaskRepeat.NextDue("X-KADO=WEEK-LAST-WORKDAY", D(2025, 10, 6), D(2025, 10, 8), Weekday, DayOfWeek.Wednesday));
    }

    [Fact]
    public void 稼働日が1日も無い週は飛ばして次の週へ()
    {
        // 10/13〜10/17 は全部休み。10/6(月)期限 → 10/20(月)
        bool ClosedWeek(DateOnly date) => Weekday(date) && (date < D(2025, 10, 13) || date > D(2025, 10, 17));

        Assert.Equal(D(2025, 10, 20),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 6), ClosedWeek));
        Assert.Equal(D(2025, 10, 24),
            TaskRepeat.NextDue("X-KADO=WEEK-LAST-WORKDAY", D(2025, 10, 10), D(2025, 10, 10), ClosedWeek));
    }

    // ------------------------------------------------------------------
    // 稼働日基準（月）
    // ------------------------------------------------------------------

    [Fact]
    public void 月初はその月の最初の稼働日で祝日を飛ばす()
    {
        // 2025/11/1(土)・11/2(日)・11/3(月・文化の日) → 11/4(火)
        Assert.Equal(D(2025, 11, 4),
            TaskRepeat.NextDue("X-KADO=MONTH-FIRST-WORKDAY", D(2025, 10, 1), D(2025, 10, 1)));
    }

    [Fact]
    public void 月初は期限より後で今日以降のうち最初の月の最初の稼働日()
    {
        // 期限 10/1(水)。10/15 に完了 → 11/4
        Assert.Equal(D(2025, 11, 4),
            TaskRepeat.NextDue("X-KADO=MONTH-FIRST-WORKDAY", D(2025, 10, 1), D(2025, 10, 15)));

        // 前月の月末が期限（9/30）で、10/1 に完了 → 10/1 が今日以降かつ期限より後の最初の稼働日
        Assert.Equal(D(2025, 10, 1),
            TaskRepeat.NextDue("X-KADO=MONTH-FIRST-WORKDAY", D(2025, 9, 30), D(2025, 10, 1)));
    }

    [Fact]
    public void 月末はその月の最後の稼働日()
    {
        // 期限 10/31(金)。次は 11月の最後の稼働日。11/30(日)→11/29(土)→11/28(金)
        Assert.Equal(D(2025, 11, 28),
            TaskRepeat.NextDue("X-KADO=MONTH-LAST-WORKDAY", D(2025, 10, 31), D(2025, 10, 31)));

        // 同じ月の最後の稼働日が期限より後なら、その月のうちに来る
        Assert.Equal(D(2025, 10, 31),
            TaskRepeat.NextDue("X-KADO=MONTH-LAST-WORKDAY", D(2025, 10, 15), D(2025, 10, 15)));
    }

    [Fact]
    public void 稼働日が1日も無い月は飛ばして次の月へ()
    {
        // 11月は全部休み
        bool NovemberOff(DateOnly date) => Weekday(date) && date.Month != 11;

        Assert.Equal(D(2025, 12, 1),
            TaskRepeat.NextDue("X-KADO=MONTH-FIRST-WORKDAY", D(2025, 10, 1), D(2025, 10, 1), NovemberOff));
        Assert.Equal(D(2025, 12, 31),
            TaskRepeat.NextDue("X-KADO=MONTH-LAST-WORKDAY", D(2025, 10, 31), D(2025, 10, 31), NovemberOff));
    }

    [Fact]
    public void 稼働日が1つも見つからなくても止まらずに該当なしを返す()
    {
        Assert.Null(TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 6), _ => false));
        Assert.Null(TaskRepeat.NextDue("X-KADO=MONTH-LAST-WORKDAY", D(2025, 10, 6), D(2025, 10, 6), _ => false));
    }

    [Fact]
    public void 稼働日の判定を渡さなければ土日と祝日を除いた日()
    {
        // 2025/5/3〜5/6 は憲法記念日・みどりの日・こどもの日・振替休日と土日が続く。
        // 5月の最初の稼働日は 5/1(木)。6月の最初の稼働日は 6/2(月)
        Assert.Equal(D(2025, 6, 2),
            TaskRepeat.NextDue("X-KADO=MONTH-FIRST-WORKDAY", D(2025, 5, 1), D(2025, 5, 1)));
    }

    // ------------------------------------------------------------------
    // 稼働日の判定
    // ------------------------------------------------------------------

    [Fact]
    public void 稼働日の登録がある期間は登録が決め無い期間は土日祝を除く()
    {
        // 2025/10/13(月・スポーツの日)は登録の上では稼働日。10/14 以降は登録が無い
        var calendar = WorkingDayCalendar.Create([D(2025, 10, 13)], D(2025, 10, 13), D(2025, 10, 13), null, null, null);
        var rule = new WorkdayRule(calendar);

        Assert.True(rule.IsWorkday(D(2025, 10, 13)));      // 祝日でも登録が稼働と言えば稼働
        Assert.True(rule.IsWorkday(D(2025, 10, 14)));      // 登録の外。火曜で祝日でもない
        Assert.False(rule.IsWorkday(D(2025, 10, 12)));     // 登録の外。日曜
        Assert.False(rule.IsWorkday(D(2025, 11, 3)));      // 登録の外。文化の日
    }

    [Fact]
    public void 空の稼働日カレンダーでも土日祝を除いた日を稼働日とみなす()
    {
        var rule = new WorkdayRule(WorkingDayCalendar.Empty);

        Assert.True(rule.IsWorkday(D(2025, 10, 6)));
        Assert.False(rule.IsWorkday(D(2025, 10, 11)));     // 土曜
        Assert.False(rule.IsWorkday(D(2025, 10, 13)));     // スポーツの日
        Assert.False(WorkdayRule.Default.IsWorkday(D(2025, 10, 13)));
    }

    [Fact]
    public void 稼働日の登録がある期間で休みの日は稼働日ではない()
    {
        // 登録は 10/6 だけ。範囲の中（10/6〜10/8）で 10/7 は登録が無い＝休み（平日でも）
        var calendar = WorkingDayCalendar.Create([D(2025, 10, 6), D(2025, 10, 8)], D(2025, 10, 6), D(2025, 10, 8), null, null, null);
        var rule = new WorkdayRule(calendar);

        Assert.False(rule.IsWorkday(D(2025, 10, 7)));

        // 次の回の計算にも効く：週始め（日曜始まり）は 10/6 だが、期限が 10/6 なら次の週
        Assert.Equal(D(2025, 10, 14),
            TaskRepeat.NextDue("X-KADO=WEEK-FIRST-WORKDAY", D(2025, 10, 6), D(2025, 10, 6), rule.IsWorkday));
    }
}
