namespace Kado.Presentation.Menus;

/// <summary>右クリックメニューの「期限を変える」に並べる早入れの種類。</summary>
public enum TaskDueKind
{
    /// <summary>今日。</summary>
    Today,

    /// <summary>明日。</summary>
    Tomorrow,

    /// <summary>今日より後の最初の稼働日。</summary>
    NextWorkday,

    /// <summary>来週の週始め。週の始まりの設定で決まる週の、最初の稼働日。</summary>
    NextWeekStart,

    /// <summary>期限なし。</summary>
    None,
}

/// <summary>期限の早入れ1件。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Label">メニューに出す名前。</param>
/// <param name="Due">この種類が指す日。「期限なし」と、日が見つからなかったものは null。</param>
public sealed record TaskDuePreset(TaskDueKind Kind, string Label, DateOnly? Due);

/// <summary>
/// 右クリックメニューの「期限を変える」の選択肢を決める。
/// <para>
/// 稼働日は、会社の稼働日データを優先し、データの無い期間は土日・祝日を除いた日とみなす
/// （<c>WorkdayRule</c>。タスクの繰り返しと同じ決まり）。<b>今日の日付も稼働日の判定も外から受ける</b>
/// ので、時刻に頼らずに試せる。
/// </para>
/// </summary>
public static class TaskDuePresets
{
    /// <summary>稼働日を探す日数の上限。全部が休みの期間が続いても、探し続けて止まらなくならないための歯止め。</summary>
    private const int SearchDays = 366;

    /// <summary>メニューに並べる5件（今日・明日・次の稼働日・来週の週始め・期限なし）。</summary>
    /// <param name="today">今日。</param>
    /// <param name="weekStart">週の始まりの曜日（設定）。</param>
    /// <param name="isWorkday">稼働日か。</param>
    public static IReadOnlyList<TaskDuePreset> For(DateOnly today, DayOfWeek weekStart, Func<DateOnly, bool> isWorkday)
    {
        ArgumentNullException.ThrowIfNull(isWorkday);

        return
        [
            new(TaskDueKind.Today, "今日", today),
            new(TaskDueKind.Tomorrow, "明日", today.AddDays(1)),
            new(TaskDueKind.NextWorkday, "次の稼働日", NextWorkday(today, isWorkday)),
            new(TaskDueKind.NextWeekStart, "来週の週始め（最初の稼働日）", NextWeekFirstWorkday(today, weekStart, isWorkday)),
            new(TaskDueKind.None, "期限なし", null),
        ];
    }

    /// <summary>今日より後の最初の稼働日。見つからなければ null。</summary>
    public static DateOnly? NextWorkday(DateOnly today, Func<DateOnly, bool> isWorkday) =>
        FirstWorkdayFrom(today.AddDays(1), isWorkday);

    /// <summary>
    /// 来週の週始め。週の始まりの曜日から数えた次の週の、最初の稼働日。
    /// <para>来週が丸ごと休みなら、その次の週へ進む（休みの週に期限を置かない）。</para>
    /// </summary>
    public static DateOnly? NextWeekFirstWorkday(DateOnly today, DayOfWeek weekStart, Func<DateOnly, bool> isWorkday)
    {
        var thisWeekStart = today.AddDays(-(((int)today.DayOfWeek - (int)weekStart + 7) % 7));

        return FirstWorkdayFrom(thisWeekStart.AddDays(7), isWorkday);
    }

    private static DateOnly? FirstWorkdayFrom(DateOnly start, Func<DateOnly, bool> isWorkday)
    {
        for (var i = 0; i < SearchDays; i++)
        {
            var date = start.AddDays(i);

            if (isWorkday(date)) return date;
        }

        return null;
    }
}
