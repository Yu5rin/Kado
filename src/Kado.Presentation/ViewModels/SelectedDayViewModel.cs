using System.Globalization;
using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

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
public sealed class TaskListItemViewModel(TaskItem task, DueText? due, DoneText? done = null)
{
    /// <summary>元のタスク。</summary>
    public TaskItem Task { get; } = task;

    public string Id => Task.Id;
    public string Title => Task.Title;
    public bool IsDone => Task.IsDone;
    public string? Note => Task.Note;

    /// <summary>
    /// 「残り 3実働日」などの表示。期限が無ければ null。
    /// <para>
    /// <b>済んだタスクには出さない。</b>片付いているのに「3日 遅れ」と出たままでは、
    /// まだ残っているように読める。済んだあとに要るのは結果のほう
    /// （<see cref="DoneText"/>）で、そちらを出す。
    /// </para>
    /// </summary>
    public string? DueText => IsDone ? null : due?.Text;

    /// <summary>
    /// 「2実働日 遅れて完了」などの結果。済んでいない、または期限が無ければ null。
    /// <para>
    /// 期限に間に合ったかどうかは、片付いたあとで振り返るときに要る。Google ToDo が
    /// 期限と完了日時の両方を持っているので、その差から出している。
    /// </para>
    /// </summary>
    public string? DoneText => done?.Text;

    /// <summary>遅れて済ませたか。文字の色を変えるのに使う。</summary>
    public bool IsLate => done is { Kind: DoneKind.Late };

    /// <summary>期限より早く済ませたか。</summary>
    public bool IsEarly => done is { Kind: DoneKind.Early };

    /// <summary>
    /// 期限の右に添える期限日。「残り 5実働日」だけでは何日なのか分からないため。
    /// <para>
    /// <b>期限が無いとき・今日のときは出さない。</b>ここは以前
    /// <see cref="TaskItem.TaskListId"/> に倒れており、期限が今日のタスクを足すと
    /// 「今日まで MDA2MTA2…」とタスクリストの識別子がそのまま並んでいた。
    /// 識別子は Google が振った不透明な文字列で、読めるものではない。
    /// 今日のぶんは <c>DueText</c> の「今日まで」で足りるので、何も添えない。
    /// </para>
    /// </summary>
    public string? DueSubText
    {
        get
        {
            if (Task.Due is not { } d || due is { Kind: DueKind.Today }) return null;

            return d.Year == DateTime.Today.Year
                ? d.ToString("M/d", CultureInfo.InvariantCulture)
                : d.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
        }
    }

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
    private IReadOnlyList<TaskListItemViewModel> _noDueTasks = [];
    private IReadOnlyList<MilestoneViewModel> _milestones = [];

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
            // 選んだ日が変わっただけなら、日付に依存する部分だけ組み直す。
            // 期限なしタスクの一覧（NoDueTasks）は選択日を見ていないので触らない
            if (Set(ref _date, value)) RefreshDateDependent();
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

    /// <summary>この日の祝日名。祝日でなければ null。予定ではなく添え書きとして出す。</summary>
    public string? HolidayName => _workspace.Holidays.NameOf(_date);

    /// <summary>非稼働日か。データ範囲外は false（判断できないため）。</summary>
    public bool IsNonWorkingDay =>
        _workspace.WorkingDays.HasDataFor(_date) && !_workspace.WorkingDays.IsWorkingDay(_date);

    /// <summary>
    /// この日のマイルストーン。左パネルのチェックに従う。
    /// <para>
    /// 読まれるたびに DB を引く計算プロパティだったのを、<see cref="RefreshDateDependent"/>
    /// で1回だけ計算してフィールドに持つ形に変えた。バインドは何度も読みに来る
    /// （右ペインの描画のたびなど）ので、そのたびに問い合わせていた。
    /// </para>
    /// </summary>
    public IReadOnlyList<MilestoneViewModel> Milestones => _milestones;

    /// <summary>この日の予定。</summary>
    public IReadOnlyList<DayEventViewModel> Events
    {
        get => _events;
        private set => Set(ref _events, value);
    }

    /// <summary>見出しの右に出す予定の件数。</summary>
    public string EventCountText => _events.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>予定が1件もないか。0件のときの案内を出すかどうかに使う（項目12）。</summary>
    public bool HasNoEvents => _events.Count == 0;

    /// <summary>
    /// 見出しの右に出す「3 / 4」。<b>残っている数と全体</b>。
    /// <para>片付いた数より、あと何件あるかのほうが知りたい。</para>
    /// </summary>
    public string TaskCountText => $"{RemainingTaskCount} / {_tasks.Count}";

    /// <summary>
    /// 「月末まで 5実働日」。今日から月末までの残り。データが無ければ null。
    /// <para>選択日ではなく今日を起点にする。あとどれだけ働けるかを知りたいので。</para>
    /// <para>暦日で数える設定なら「月末まで 9日」。こちらは実働日データが要らない。</para>
    /// </summary>
    public string? RemainingInMonthText
    {
        get
        {
            if (_workspace.CountInCalendarDays)
            {
                var last = DateTime.DaysInMonth(_today.Year, _today.Month);
                return $"月末まで {last - _today.Day}日";
            }

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
    /// <para>
    /// 並びは<b>期限の近い順</b>。遅れているものが一番上に来る。同じ期限日の中は
    /// 並び順（手で並べ替えていなければ登録が古い順）で並べる。あとから足した
    /// タスクが題名の並びで上に割り込まないようにするため。
    /// </para>
    /// <para>期限の無いタスクはここには出さない。並べる順番が決まらないため。</para>
    /// </summary>
    public IReadOnlyList<TaskListItemViewModel> Tasks
    {
        get => _tasks;
        private set => Set(ref _tasks, value);
    }

    /// <summary>タスクが1件もないか。0件のときの案内を出すかどうかに使う（項目12）。</summary>
    public bool HasNoTasks => _tasks.Count == 0;

    /// <summary>完了した数。</summary>
    public int DoneTaskCount => _tasks.Count(t => t.IsDone);

    /// <summary>まだ残っている数。見出しの分子。</summary>
    public int RemainingTaskCount => _tasks.Count(t => !t.IsDone);

    /// <summary>
    /// 期限を付けていないタスク（要件書 3.1 の「いつやるか未定」）。
    /// <para>
    /// <see cref="Tasks"/> は期限が無いと外れてしまい、編集画面で「期限を付ける」を
    /// 外すと二度と画面に出せなくなっていた。ここに別のまとまりとして出し、
    /// 編集・削除・完了の切り替えができるようにする。
    /// </para>
    /// <para>
    /// 完了済みは出さない。期限が無いままでは「いつ片付けたか」を表示する場所が
    /// 無く、<see cref="Tasks"/> 側のように選択日で絞ることもできないため。
    /// </para>
    /// </summary>
    public IReadOnlyList<TaskListItemViewModel> NoDueTasks
    {
        get => _noDueTasks;
        private set => Set(ref _noDueTasks, value);
    }

    /// <summary>期限なしタスクの件数。見出しに出す。</summary>
    public int NoDueTaskCount => _noDueTasks.Count;

    /// <summary>
    /// 期限なしタスクの節を出すか。1件もなければ節ごと畳む。
    /// <para>
    /// 既存の <see cref="Tasks"/> と違い、無い人には無関係な機能なので、
    /// 空でも案内文を出す必要は無い（項目12は既存の「タスク」節でまかなう）。
    /// </para>
    /// </summary>
    public bool ShowsNoDueTasks => _noDueTasks.Count > 0;

    /// <summary>
    /// 済んだタスクの結果。期限か完了日時が無ければ null。
    /// <para>Google から来たタスクは完了日時を持っている。こちらで片付けたものも控えてある。</para>
    /// </summary>
    private DoneText? DoneOf(TaskItem task) =>
        task is { IsDone: true, Due: { } due, CompletedAt: { } at }
            ? _workspace.DueFormatter.FormatDone(due, DateOnly.FromDateTime(at.LocalDateTime))
            : null;

    /// <summary>
    /// データが変わった・「今日」が変わったときに、すべて読み直す。
    /// <para>
    /// <see cref="Kado.Data.Repositories.TaskRepository.All"/> は呼ぶたびに DB を1回
    /// 読む。以前は <see cref="RefreshDateDependent"/> と期限なしタスクの組み立てで
    /// それぞれ呼んでおり、1回の Refresh で2回読んでいた。ここで1回だけ読み、
    /// 両方に使い回す。
    /// </para>
    /// </summary>
    public void Refresh()
    {
        var allTasks = _workspace.Tasks.All();

        RefreshDateDependent(allTasks);
        RefreshNoDueTasks(allTasks);
    }

    /// <summary>
    /// 選んでいる日に依存する部分だけ組み直す。
    /// <para>
    /// 予定（<see cref="Events"/>）・マイルストーン・タスクの一覧（完了済みは選択日の
    /// ぶんだけ添えるので、こちらも選択日に効く）が対象。<see cref="NoDueTasks"/>
    /// （期限なしの未完了タスク）は選択日を見ていないので、ここでは触らない
    /// （<see cref="Date"/> の setter が呼ぶときは日を送っただけなので、そちらは
    /// 前のままでよい）。
    /// </para>
    /// </summary>
    /// <param name="allTasks">
    /// 呼び出し側ですでに読んでいれば渡す。<see cref="Refresh"/> から渡された1回ぶんを
    /// 使い回し、<see cref="Date"/> の setter から呼ばれたときだけここで読み直す。
    /// </param>
    private void RefreshDateDependent(IReadOnlyList<TaskItem>? allTasks = null)
    {
        // 予定とマイルストーンは同じ範囲（選んでいる日1日）を見るので、問い合わせを共有する。
        // 以前は Milestones が読まれるたびに別クエリを投げていた
        var eventsOnDate = _workspace.Schedule.EventsInRange(_date, _date);

        Events = EventOrder
            .Sort(eventsOnDate.Where(e => _sources.IncludesEvent(e.Source)), _sources)
            .Select(e => new DayEventViewModel(e, _sources.ColorOf(e.Source.CalendarId)))
            .ToArray();

        _milestones = MilestoneRow.For(_date, eventsOnDate, _sources);

        Tasks = (allTasks ?? _workspace.Tasks.All())
            .Where(_sources.IncludesTask)
            .Where(t => t.HasDue)
            // 完了済みはその日に片付いたものだけ添える。過去の完了が積み上がると読めない
            .Where(t => !t.IsDone || t.Due == _date)
            // 期限の近い順。同じ期限日の中は並び順→作成日時→識別子の順
            // （既定は登録が古い順。Id まで見るのは、並びが毎回同じになる保証のため）
            .OrderBy(t => t.Due)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new TaskListItemViewModel(
                t,
                t.Due is { } due ? _workspace.DueFormatter.Format(due, _today) : null,
                DoneOf(t)))
            .ToArray();

        Raise(nameof(Title), nameof(WorkingDayLabel), nameof(IsNonWorkingDay),
              nameof(Milestones), nameof(HolidayName), nameof(DoneTaskCount), nameof(RemainingTaskCount),
              nameof(EventCountText), nameof(TaskCountText), nameof(RemainingInMonthText),
              nameof(HasNoEvents), nameof(HasNoTasks));
    }

    /// <summary>
    /// 期限を付けていない、未完了のタスク（項目1）を組み直す。
    /// <para>
    /// 選んでいる日を見ないので、<see cref="Date"/> が変わっただけのときは
    /// 呼ばない。並びは並び順→作成日時→識別子（既定は登録が古い順）。
    /// 期限のあるタスクと同じ考え方。
    /// </para>
    /// </summary>
    /// <param name="allTasks"><see cref="Refresh"/> から渡された1回ぶんの読み直し結果。</param>
    private void RefreshNoDueTasks(IReadOnlyList<TaskItem>? allTasks = null)
    {
        NoDueTasks = (allTasks ?? _workspace.Tasks.All())
            .Where(_sources.IncludesTask)
            .Where(t => !t.HasDue && !t.IsDone)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new TaskListItemViewModel(t, due: null))
            .ToArray();

        Raise(nameof(NoDueTaskCount), nameof(ShowsNoDueTasks));
    }

    private static readonly string[] JapaneseDayNames = ["日", "月", "火", "水", "木", "金", "土"];
}
