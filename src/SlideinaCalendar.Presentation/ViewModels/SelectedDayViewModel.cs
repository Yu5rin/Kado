using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>期限の表示に色を付けるための区分。</summary>
public enum DueEmphasis
{
    /// <summary>まだ余裕がある。</summary>
    Normal,

    /// <summary>今日が期限。</summary>
    Today,

    /// <summary>過ぎている。赤で出す。</summary>
    Overdue,
}

/// <summary>右ペインに並べるタスク1件。</summary>
public sealed class TaskListItemViewModel(TaskItem task, DueText? due)
{
    /// <summary>元のタスク。</summary>
    public TaskItem Task { get; } = task;

    public string Id => Task.Id;
    public string Title => Task.Title;
    public bool IsDone => Task.IsDone;
    public string? Note => Task.Note;

    /// <summary>「残り 3実働日」などの表示。期限が無ければ null。</summary>
    public string? DueText => due?.Text;

    /// <summary>期限の強調度。</summary>
    public DueEmphasis Emphasis => due?.Kind switch
    {
        DueKind.Overdue => DueEmphasis.Overdue,
        DueKind.Today => DueEmphasis.Today,
        _ => DueEmphasis.Normal,
    };

    /// <summary>
    /// 補足の説明。実働日データが無い期間は暦日で数えている旨を伝える
    /// （要件書 4.4 の「ツールチップで未登録である旨を補足」）。
    /// </summary>
    public string? DueTooltip => due switch
    {
        null => null,
        { Kind: DueKind.CalendarDays } => "この期間は実働日データが未登録のため、暦日で数えています。",
        { IsSnapped: true } d =>
            $"期限の {Task.Due:M/d} は非稼働日のため、直前の実働日 {d.EffectiveDue:M/d} までで数えています。",
        _ => null,
    };
}

/// <summary>
/// 右ペイン。選択した日の予定とタスクを<b>上下に同時表示</b>する（要件書 5.2）。
/// 幅があるのでタブにはしない。
/// </summary>
public sealed class SelectedDayViewModel : ObservableObject
{
    private readonly CalendarWorkspace _workspace;

    private DateOnly _date;
    private DateOnly _today;
    private IReadOnlyList<ScheduledEvent> _events = [];
    private IReadOnlyList<TaskListItemViewModel> _tasks = [];

    public SelectedDayViewModel(CalendarWorkspace workspace, DateOnly date, DateOnly today)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _date = date;
        _today = today;

        Refresh();
    }

    /// <summary>表示している日。</summary>
    public DateOnly Date
    {
        get => _date;
        set
        {
            if (Set(ref _date, value)) Refresh();
        }
    }

    /// <summary>今日。</summary>
    public DateOnly Today
    {
        get => _today;
        set
        {
            if (Set(ref _today, value)) Refresh();
        }
    }

    /// <summary>「9月24日（木）」のような見出し。</summary>
    public string Title =>
        $"{_date.ToString("M月d日", CultureInfo.InvariantCulture)}（{JapaneseDayNames[(int)_date.DayOfWeek]}）";

    /// <summary>
    /// 「実働 17日目」のような表示。非稼働日とデータ範囲外は null。
    /// <para>選択日ヘッダは通し番号を出す場所のひとつ（要件書 4.3）。</para>
    /// </summary>
    public string? WorkingDayLabel =>
        _workspace.WorkingDays.IndexInMonth(_date) is { } index ? $"実働 {index}日目" : null;

    /// <summary>非稼働日か。データ範囲外は false（判断できないため）。</summary>
    public bool IsNonWorkingDay =>
        _workspace.WorkingDays.HasDataFor(_date) && !_workspace.WorkingDays.IsWorkingDay(_date);

    /// <summary>この日のマイルストーン。</summary>
    public IReadOnlyList<Milestone> Milestones => _workspace.WorkingDays.MilestonesOn(_date);

    /// <summary>この日の予定。</summary>
    public IReadOnlyList<ScheduledEvent> Events
    {
        get => _events;
        private set => Set(ref _events, value);
    }

    /// <summary>この日が期限のタスク。</summary>
    public IReadOnlyList<TaskListItemViewModel> Tasks
    {
        get => _tasks;
        private set => Set(ref _tasks, value);
    }

    /// <summary>「タスク 3/4」の分子。完了した数。</summary>
    public int DoneTaskCount => _tasks.Count(t => t.IsDone);

    /// <summary>読み直す。</summary>
    public void Refresh()
    {
        Events = _workspace.Schedule.EventsInRange(_date, _date);

        Tasks = _workspace.Tasks.DueInRange(_date, _date)
            .Select(t => new TaskListItemViewModel(
                t, t.Due is { } due ? _workspace.DueFormatter.Format(due, _today) : null))
            .ToArray();

        Raise(nameof(Title), nameof(WorkingDayLabel), nameof(IsNonWorkingDay),
              nameof(Milestones), nameof(DoneTaskCount));
    }

    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];
}
