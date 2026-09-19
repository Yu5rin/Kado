using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 月ビューの1マス。
/// <para>
/// <b>実働日の通し番号はここには出さない。</b>マイルストーンの表示と競合して密度が
/// 上がりすぎるため、月ビューと年ビューでは出さず、ツールバーのサマリー・右ペインの
/// 選択日ヘッダ・週ビューの日付ヘッダ・一覧ビューの日付列に出す（要件書 4.3）。
/// 添付のモックはこれより前の案で、セル右上に番号を置いている。仕様の正は要件書のほう。
/// </para>
/// </summary>
public sealed class DayCellViewModel : ObservableObject
{
    /// <summary>マスに並べる最大件数。これを超えたぶんは「＋N」にまとめる。</summary>
    public const int DefaultMaxChips = 3;

    private bool _isSelected;

    public DayCellViewModel(
        DateOnly date,
        bool isCurrentMonth,
        DateOnly today,
        WorkingDayCalendar workingDays,
        IReadOnlyList<ScheduledEvent> events,
        IReadOnlyList<TaskItem> tasks,
        string? holidayName = null,
        ICalendarPalette? palette = null,
        int maxChips = DefaultMaxChips,
        IReadOnlyList<MilestoneViewModel>? milestones = null)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = date == today;
        HolidayName = holidayName;

        AllEvents = events;
        AllTasks = tasks;

        // マスに入る数には限りがある。溢れたぶんは「＋N」でまとめて示し、
        // 件数が分からないまま隠れてしまうのを避ける
        var chips = events
            .Select(e => new EventChipViewModel(e, palette?.ColorOf(e.Source.CalendarId)))
            .ToArray();
        var taskChips = tasks;

        var total = chips.Length + taskChips.Count;
        if (total <= maxChips)
        {
            Events = chips;
            Tasks = taskChips;
            OverflowCount = 0;
        }
        else
        {
            // 予定を先に見せる。タスクは右ペインでも一覧できる
            var eventRoom = Math.Min(chips.Length, maxChips);
            Events = chips.Take(eventRoom).ToArray();
            Tasks = taskChips.Take(maxChips - eventRoom).ToArray();
            OverflowCount = total - maxChips;
        }

        HasWorkingDayData = workingDays.HasDataFor(date);
        IsWorkingDay = workingDays.IsWorkingDay(date);
        // 日付の行に出すものは呼び出し側が組み立てる（左パネルのチェックを効かせるため）。
        // 渡されなければ実働日データから直に引く
        Milestones = milestones ?? [];
    }

    /// <summary>この日。</summary>
    public DateOnly Date { get; }

    /// <summary>表示している月の日か。前後の月にはみ出した分は false。</summary>
    public bool IsCurrentMonth { get; }

    /// <summary>今日か。</summary>
    public bool IsToday { get; }

    /// <summary>選択中か。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>実働日データの登録範囲内か。範囲外は稼働・非稼働を判断できない。</summary>
    public bool HasWorkingDayData { get; }

    /// <summary>稼働日か。</summary>
    public bool IsWorkingDay { get; }

    /// <summary>
    /// 背景を沈めるか。
    /// <para>
    /// 非稼働日は色を足さず背景を落とす（要件書 4.3）。祝日の赤や予定の色と競合させない
    /// ための引き算。データが無い日は判断できないので沈めない。
    /// </para>
    /// </summary>
    public bool IsDimmed => HasWorkingDayData && !IsWorkingDay;

    /// <summary>
    /// 稼働する日として面を起こすか。
    /// <para>
    /// 休業日と見分けるため。<b>データが無い日とも見分ける</b>ので、どこまで登録済みかが
    /// 面の色だけで読める。
    /// </para>
    /// </summary>
    public bool IsWorkingDayLit => HasWorkingDayData && IsWorkingDay;

    /// <summary>日曜か。</summary>
    public bool IsSunday => Date.DayOfWeek == DayOfWeek.Sunday;

    /// <summary>
    /// 日曜と同じ赤で出すか。日曜と祝日。
    /// <para>
    /// 祝日は曜日に関わらず赤。土曜に重なっても赤を採る。祝日であることのほうが、
    /// その日の予定の立て方に効く。
    /// </para>
    /// </summary>
    public bool IsSundayLike => IsSunday || HolidayName is { Length: > 0 };

    /// <summary>土曜か。</summary>
    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    /// <summary>この日のマイルストーン。</summary>
    public IReadOnlyList<MilestoneViewModel> Milestones { get; }

    /// <summary>マスに並べる予定。溢れたぶんは含まない。</summary>
    public IReadOnlyList<EventChipViewModel> Events { get; }

    /// <summary>マスに並べるタスク。溢れたぶんは含まない。</summary>
    public IReadOnlyList<TaskItem> Tasks { get; }

    /// <summary>この日の予定すべて。</summary>
    public IReadOnlyList<ScheduledEvent> AllEvents { get; }

    /// <summary>この日が期限のタスクすべて。</summary>
    public IReadOnlyList<TaskItem> AllTasks { get; }

    /// <summary>マスに入りきらなかった件数。0 なら省略は起きていない。</summary>
    public int OverflowCount { get; }

    /// <summary>「＋2」の表示。溢れていなければ null。</summary>
    public string? OverflowLabel => OverflowCount > 0 ? $"＋{OverflowCount}" : null;

    /// <summary>
    /// 祝日の名前。祝日でなければ null。
    /// <para>日付の下に赤で出す。休みの理由が読めると予定を立てやすい。</para>
    /// </summary>
    public string? HolidayName { get; }

    /// <summary>日付の数字。</summary>
    public int DayNumber => Date.Day;

    /// <summary>マスに何も無いか。</summary>
    public bool IsEmpty =>
        AllEvents.Count == 0 && AllTasks.Count == 0 && Milestones.Count == 0 && HolidayName is null;
}
