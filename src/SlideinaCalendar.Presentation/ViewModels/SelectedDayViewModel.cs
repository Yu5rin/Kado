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

    /// <summary>
    /// 期限の右に添える文字。モックは期限日そのものかタスクリスト名を出している。
    /// 「残り 5実働日」だけでは何日なのか分からないため。
    /// </summary>
    public string? DueSubText => Task.Due is { } d && due is { Kind: not DueKind.Today }
        ? d.ToString("yyyy/M/d", CultureInfo.InvariantCulture) is var full && d.Year == DateTime.Today.Year
            ? d.ToString("M/d", CultureInfo.InvariantCulture)
            : full
        : Task.TaskListId;

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
/// 右ペインに並べる予定1件。
/// <para>
/// モックは時刻・縦棒・タイトルの下に「第2会議室 ・ 1時間30分」という補助行を出す。
/// 場所と長さはその場で判断したい情報なので、開かずに読めるようにする。
/// </para>
/// </summary>
public sealed class DayEventViewModel(ScheduledEvent scheduled, string? color = null)
{
    /// <summary>元の予定。</summary>
    public ScheduledEvent Scheduled { get; } = scheduled;

    public string Id => Scheduled.Source.Id;
    public string Title => Scheduled.Source.Title;

    /// <summary>「09:00」。終日なら「終日」。</summary>
    public string TimeText => Scheduled.Source.StartTime is { } start
        ? start.ToString("HH:mm", CultureInfo.InvariantCulture)
        : "終日";

    /// <summary>帯の色（<c>#rrggbb</c>）。所属カレンダーで決まる。null なら既定のアクセント色。</summary>
    public string? Color { get; } = color;

    /// <summary>「第2会議室 ・ 1時間30分」。どちらも無ければ null。</summary>
    public string? SubText
    {
        get
        {
            var parts = new List<string>(2);

            if (Scheduled.Source.Location is { Length: > 0 } location) parts.Add(location);
            if (DurationText is { } duration) parts.Add(duration);

            return parts.Count > 0 ? string.Join(" ・ ", parts) : null;
        }
    }

    /// <summary>「1時間30分」。時刻が入っていなければ null。</summary>
    private string? DurationText
    {
        get
        {
            if (Scheduled.Source.StartTime is not { } start ||
                Scheduled.Source.EndTime is not { } end) return null;

            var minutes = (int)(end - start).TotalMinutes;
            if (minutes <= 0) return null;

            var (h, m) = (minutes / 60, minutes % 60);
            return (h, m) switch
            {
                (0, _) => $"{m}分",
                (_, 0) => $"{h}時間",
                _ => $"{h}時間{m}分",
            };
        }
    }
}

/// <summary>
/// 右ペイン。選択した日の予定とタスクを<b>上下に同時表示</b>する（要件書 5.2）。
/// 幅があるのでタブにはしない。
/// </summary>
public sealed class SelectedDayViewModel : ObservableObject
{
    private readonly CalendarWorkspace _workspace;
    private readonly ICalendarSources _sources;

    private DateOnly _date;
    private DateOnly _today;
    private IReadOnlyList<DayEventViewModel> _events = [];
    private IReadOnlyList<TaskListItemViewModel> _tasks = [];

    public SelectedDayViewModel(CalendarWorkspace workspace, DateOnly date, DateOnly today,
        ICalendarSources? sources = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sources = sources ?? DefaultCalendarSources.Instance;
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

    /// <summary>この日のマイルストーン。左パネルのチェックに従う。</summary>
    public IReadOnlyList<Milestone> Milestones =>
        MilestoneRow.For(_date, _workspace.Schedule.EventsInRange(_date, _date), _sources);

    /// <summary>この日の予定。</summary>
    public IReadOnlyList<DayEventViewModel> Events
    {
        get => _events;
        private set => Set(ref _events, value);
    }

    /// <summary>見出しの右に出す予定の件数。</summary>
    public string EventCountText => _events.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 見出しの右に出す「3 / 4」。<b>残っている数と全体</b>。
    /// <para>片付いた数より、あと何件あるかのほうが知りたい。</para>
    /// </summary>
    public string TaskCountText => $"{RemainingTaskCount} / {_tasks.Count}";

    /// <summary>
    /// 「月末まで 5実働日」。今日から月末までの残り。データが無ければ null。
    /// <para>選択日ではなく今日を起点にする。あとどれだけ働けるかを知りたいので。</para>
    /// </summary>
    public string? RemainingInMonthText
    {
        get
        {
            if (!_workspace.WorkingDays.IsMonthFullyCovered(_today.Year, _today.Month)) return null;

            return $"月末まで {_workspace.WorkingDays.RemainingInMonth(_today)}実働日";
        }
    }

    /// <summary>
    /// 右ペインに並べるタスク。
    /// <para>
    /// <b>その日が期限のものだけではない。</b>未完了で期限のあるタスクを期限の早い順に
    /// すべて出し、完了済みは選択日が期限のものだけ添える。先の期限が見えないと、
    /// 今日やることは分かっても段取りが組めない（モックの右ペインも 10/1 や
    /// 2027/3/31 期限のタスクを並べている）。
    /// </para>
    /// <para>並びは<b>期限の近い順</b>。遅れているものが一番上に来る。</para>
    /// <para>期限の無いタスクはここには出さない。並べる順番が決まらないため。</para>
    /// </summary>
    public IReadOnlyList<TaskListItemViewModel> Tasks
    {
        get => _tasks;
        private set => Set(ref _tasks, value);
    }

    /// <summary>完了した数。</summary>
    public int DoneTaskCount => _tasks.Count(t => t.IsDone);

    /// <summary>まだ残っている数。見出しの分子。</summary>
    public int RemainingTaskCount => _tasks.Count(t => !t.IsDone);

    /// <summary>読み直す。</summary>
    public void Refresh()
    {
        Events = _workspace.Schedule.EventsInRange(_date, _date)
            .Where(e => _sources.IncludesEvent(e.Source))
            .Select(e => new DayEventViewModel(e, _sources.ColorOf(e.Source.CalendarId)))
            .ToArray();

        Tasks = _workspace.Tasks.All()
            .Where(_sources.IncludesTask)
            .Where(t => t.HasDue)
            // 完了済みはその日に片付いたものだけ添える。過去の完了が積み上がると読めない
            .Where(t => !t.IsDone || t.Due == _date)
            // 期限の近い順。遅れているものが一番上に来る
            .OrderBy(t => t.Due)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => new TaskListItemViewModel(
                t, t.Due is { } due ? _workspace.DueFormatter.Format(due, _today) : null))
            .ToArray();

        Raise(nameof(Title), nameof(WorkingDayLabel), nameof(IsNonWorkingDay),
              nameof(Milestones), nameof(DoneTaskCount), nameof(RemainingTaskCount),
              nameof(EventCountText), nameof(TaskCountText), nameof(RemainingInMonthText));
    }

    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];
}
