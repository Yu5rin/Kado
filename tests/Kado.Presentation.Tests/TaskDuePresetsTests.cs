using Kado.Core.WorkingDays;
using Kado.Presentation.Menus;

namespace Kado.Presentation.Tests;

/// <summary>
/// 「期限を変える」の早入れ。今日の日付も稼働日の判定も外から渡すので、時刻に頼らずに試せる。
/// </summary>
public class TaskDuePresetsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>土日だけが休みの暦。</summary>
    private static bool Weekdays(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    // 2026/10/7 は水曜日
    private static readonly DateOnly Wednesday = D(2026, 10, 7);

    [Fact]
    public void 五つの項目が決まった順に並ぶ()
    {
        var presets = TaskDuePresets.For(Wednesday, DayOfWeek.Sunday, Weekdays);

        Assert.Equal(
            ["今日", "明日", "次の稼働日", "来週の週始め（最初の稼働日）", "期限なし"],
            presets.Select(p => p.Label));
        Assert.Equal(
            [TaskDueKind.Today, TaskDueKind.Tomorrow, TaskDueKind.NextWorkday, TaskDueKind.NextWeekStart, TaskDueKind.None],
            presets.Select(p => p.Kind));
    }

    [Fact]
    public void 今日と明日と期限なし()
    {
        var presets = TaskDuePresets.For(Wednesday, DayOfWeek.Sunday, Weekdays);

        Assert.Equal(Wednesday, presets[0].Due);
        Assert.Equal(D(2026, 10, 8), presets[1].Due);
        Assert.Null(presets[4].Due);
    }

    [Fact]
    public void 次の稼働日は平日なら翌日()
    {
        Assert.Equal(D(2026, 10, 8), TaskDuePresets.NextWorkday(Wednesday, Weekdays));
    }

    [Fact]
    public void 次の稼働日は金曜日なら週明けの月曜日()
    {
        Assert.Equal(D(2026, 10, 12), TaskDuePresets.NextWorkday(D(2026, 10, 9), Weekdays));
    }

    [Fact]
    public void 次の稼働日は今日が休みの日でも今日より後()
    {
        // 土曜日。今日は数えず、月曜日になる
        Assert.Equal(D(2026, 10, 12), TaskDuePresets.NextWorkday(D(2026, 10, 10), Weekdays));
    }

    [Fact]
    public void 次の稼働日は稼働日データの休みを飛ばす()
    {
        // 10/8（木）と 10/9（金）が会社の休み
        bool Workday(DateOnly date) => Weekdays(date) && date != D(2026, 10, 8) && date != D(2026, 10, 9);

        Assert.Equal(D(2026, 10, 12), TaskDuePresets.NextWorkday(Wednesday, Workday));
    }

    [Fact]
    public void 来週の週始めは週の始まりが日曜日なら日曜日から数えた次の週の最初の稼働日()
    {
        // 週は日曜始まり。今週は 10/4(日)〜10/10(土)。来週の頭は 10/11(日) で、最初の稼働日は 10/12(月)
        Assert.Equal(D(2026, 10, 12), TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Sunday, Weekdays));
    }

    [Fact]
    public void 来週の週始めは週の始まりが月曜日なら月曜日()
    {
        // 週は月曜始まり。今週は 10/5(月)〜10/11(日)。来週の頭は 10/12(月)
        Assert.Equal(D(2026, 10, 12), TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Monday, Weekdays));
    }

    [Fact]
    public void 来週の週始めは週の始まりの曜日が水曜日なら今日が週の頭でも来週になる()
    {
        // 今日が水曜日で、週は水曜始まり。今週は 10/7(水)〜10/13(火)。来週の頭は 10/14(水)
        Assert.Equal(D(2026, 10, 14), TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Wednesday, Weekdays));
    }

    [Fact]
    public void 来週の頭が休みなら最初の稼働日まで進む()
    {
        // 月曜始まりの来週の頭 10/12(月)が会社の休み。最初の稼働日は 10/13(火)
        bool Workday(DateOnly date) => Weekdays(date) && date != D(2026, 10, 12);

        Assert.Equal(D(2026, 10, 13), TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Monday, Workday));
    }

    [Fact]
    public void 来週が丸ごと休みなら再来週の最初の稼働日()
    {
        // 10/12〜10/18 が全部休み（連休）
        bool Workday(DateOnly date) => Weekdays(date) && (date < D(2026, 10, 12) || date > D(2026, 10, 18));

        Assert.Equal(D(2026, 10, 19), TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Monday, Workday));
    }

    [Fact]
    public void 稼働日が見つからなければnullで止まる()
    {
        // 全部が休み。探し続けて止まらなくなることはない
        Assert.Null(TaskDuePresets.NextWorkday(Wednesday, _ => false));
        Assert.Null(TaskDuePresets.NextWeekFirstWorkday(Wednesday, DayOfWeek.Monday, _ => false));
    }

    [Fact]
    public void 稼働日の決まりは稼働日データを優先し_無い期間は土日祝を除く()
    {
        // WorkdayRule（タスクの繰り返しと同じ決まり）を渡したとき。データの無い期間は土日と祝日が休み
        var rule = new WorkdayRule(WorkingDayCalendar.Empty, date => date == D(2026, 10, 8));

        // 10/8(木)が祝日なので、水曜の次の稼働日は 10/9(金)
        Assert.Equal(D(2026, 10, 9), TaskDuePresets.NextWorkday(Wednesday, rule.IsWorkday));
    }
}
