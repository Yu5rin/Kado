using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Menus;

/// <summary>右クリックされた予定。</summary>
/// <param name="Id">予定の識別子。</param>
/// <param name="Date">右クリックされた回の日付。繰り返しの1回分を複製・コピーするのに使う。</param>
public sealed record EventEntry(string Id, DateOnly? Date);

/// <summary>日付のメニューが開かれた場所。</summary>
/// <param name="Date">その日。</param>
/// <param name="Time">時間帯の上で右クリックしたときの、15分に丸めた時刻。時間帯でなければ null。</param>
public sealed record DayTarget(DateOnly Date, TimeOnly? Time)
{
    /// <summary>
    /// 右クリックされた行の中身（画面の <c>DataContext</c>）から、日付を読む。読めなければ null。
    /// <para>行の型は画面ごとに違う（月のマス・週の見出しと時間軸の列・年の日・一覧の行・右ペイン）が、
    /// 振り分けはここが持つので、メニュー側は同じ書き方で済む（<see cref="Links.OpenTargets.From"/> と同じ作り）。</para>
    /// </summary>
    public static DayTarget? From(object? entry, TimeOnly? time = null) => entry switch
    {
        DayCellViewModel cell => new DayTarget(cell.Date, time),
        WeekDayColumnViewModel column => new DayTarget(column.Date, time),
        YearDayViewModel day => new DayTarget(day.Date, time),
        AgendaRowViewModel row => new DayTarget(row.Date, time),
        SelectedDayViewModel pane => new DayTarget(pane.Date, time),
        DateOnly date => new DayTarget(date, time),
        _ => null,
    };
}

/// <summary>
/// 右クリックされた行の中身（画面の <c>DataContext</c>）から、予定・タスクの識別子を読む。
/// <para>
/// 行の型は画面ごとに違う（月のチップ・右ペインの行・時間軸のブロックなど）。メニューを全部の画面で
/// 同じ書き方にするため、振り分けをここに集める。<b>識別子だけを取り、中身は保存されている行を
/// 読み直す</b>（表示用の複製を書き戻さないため。<c>MainViewModel.EditEventBy</c> と同じ考え方）。
/// </para>
/// </summary>
public static class EntryRefs
{
    /// <summary>予定として読む。予定でなければ null。</summary>
    public static EventEntry? EventOf(object? entry) => entry switch
    {
        EventChipViewModel chip => new EventEntry(chip.Id, chip.Scheduled.Date),
        DayEventViewModel row => new EventEntry(row.Id, row.Scheduled.Date),
        TimeBlockViewModel block => new EventEntry(block.Id, block.Date),
        MilestoneViewModel milestone => new EventEntry(milestone.Id, milestone.Date),
        ScheduledEvent scheduled => new EventEntry(scheduled.Source.Id, scheduled.Date),
        CalendarEvent value => new EventEntry(value.Id, value.Date),
        _ => null,
    };

    /// <summary>タスクとして読む。タスクでなければ null。</summary>
    public static string? TaskOf(object? entry) => entry switch
    {
        ScheduledTask task => task.Id,
        TaskListItemViewModel item => item.Id,
        TaskItem value => value.Id,
        _ => null,
    };
}
