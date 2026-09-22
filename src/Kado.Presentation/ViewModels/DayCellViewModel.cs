using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

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
    private bool _isDropTarget;

    /// <summary>
    /// マスに並べる最大件数。これを超えたぶんは「＋N」にまとめる。
    /// <para>実際の数はマスの高さから決める（<see cref="CapacityFor"/>）。これは最低限。</para>
    /// </summary>
    public const int DefaultMaxChips = 3;

    /// <summary>1件ぶんの高さ。チップの行の高さ 16 に上の余白 2 を足したもの。</summary>
    private const double ChipHeight = 18;

    /// <summary>日付の行と「＋N」に要る高さ。上下の余白を含む。</summary>
    private const double Reserved = 40;

    /// <summary>
    /// この高さのマスに何件並べられるか。
    /// <para>
    /// 固定の3件だと、画面を広げてもマスの下が空いたまま「＋1」と出る。実機で
    /// 「こんなにスペースがあるのに全て表示されない」という指摘があった。
    /// </para>
    /// <para>高さが分からないうちは <see cref="DefaultMaxChips"/>。</para>
    /// </summary>
    public static int CapacityFor(double cellHeight)
    {
        if (double.IsNaN(cellHeight) || cellHeight <= 0) return DefaultMaxChips;

        return Math.Max(1, (int)((cellHeight - Reserved) / ChipHeight));
    }

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
        IReadOnlyList<MilestoneViewModel>? milestones = null,
        IReadOnlyList<EventBandViewModel>? bands = null)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = date == today;
        HolidayName = holidayName;

        AllEvents = events;
        AllTasks = tasks;

        // またがる予定は帯として別に置く。週のあいだ同じ段に居続けさせるので、
        // ここで数を減らしたり順を入れ替えたりしない
        Bands = bands ?? [];

        var banded = Bands
            .Where(b => !b.IsEmpty)
            .Select(b => b.Chip!.Id)
            .ToHashSet(StringComparer.Ordinal);

        // マスに入る数には限りがある。溢れたぶんは「＋N」でまとめて示し、
        // 件数が分からないまま隠れてしまうのを避ける
        var chips = events
            .Where(e => !banded.Contains(e.Source.Id))
            .Select(e => new EventChipViewModel(e, palette?.ColorOf(e.Source.CalendarId)))
            .ToArray();
        var taskChips = tasks;

        // 空の段も場所を取る。帯を削ると繋がりが切れるので、削るのは帯以外から
        var room = Math.Max(0, maxChips - Bands.Count);

        var total = chips.Length + taskChips.Count;
        if (total <= room)
        {
            Events = chips;
            Tasks = taskChips;
            OverflowCount = 0;
        }
        else
        {
            // 予定を先に見せる。タスクは右ペインでも一覧できる
            var eventRoom = Math.Min(chips.Length, room);
            Events = chips.Take(eventRoom).ToArray();
            Tasks = taskChips.Take(room - eventRoom).ToArray();
            OverflowCount = total - room;
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

    /// <summary>
    /// いま何かを落とそうとしている先か。
    /// <para>掴んだものがどこへ入るのか分からないと、落とす手が止まる。面で示す。</para>
    /// </summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
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
    /// <summary>
    /// またがる予定の帯。段の位置が週のあいだ揃うよう、空の枠も含む。
    /// <para>予定のチップより上に出す。</para>
    /// </summary>
    public IReadOnlyList<EventBandViewModel> Bands { get; }

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
