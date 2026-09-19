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
    private bool _isSelected;

    public DayCellViewModel(
        DateOnly date,
        bool isCurrentMonth,
        DateOnly today,
        WorkingDayCalendar workingDays,
        IReadOnlyList<ScheduledEvent> events,
        IReadOnlyList<TaskItem> tasks)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = date == today;
        Events = events;
        Tasks = tasks;

        HasWorkingDayData = workingDays.HasDataFor(date);
        IsWorkingDay = workingDays.IsWorkingDay(date);
        Milestones = workingDays.MilestonesOn(date);
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

    /// <summary>日曜か。</summary>
    public bool IsSunday => Date.DayOfWeek == DayOfWeek.Sunday;

    /// <summary>土曜か。</summary>
    public bool IsSaturday => Date.DayOfWeek == DayOfWeek.Saturday;

    /// <summary>この日のマイルストーン。</summary>
    public IReadOnlyList<Milestone> Milestones { get; }

    /// <summary>この日の予定。</summary>
    public IReadOnlyList<ScheduledEvent> Events { get; }

    /// <summary>この日が期限のタスク。</summary>
    public IReadOnlyList<TaskItem> Tasks { get; }

    /// <summary>日付の数字。</summary>
    public int DayNumber => Date.Day;

    /// <summary>マスに何も無いか。</summary>
    public bool IsEmpty => Events.Count == 0 && Tasks.Count == 0 && Milestones.Count == 0;
}
