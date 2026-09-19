using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 表示するカレンダーとタスクリストの絞り込み。
/// <para>
/// 月ビューと右ペインはこれを見てから並べる。左パネルのチェックを外した分が
/// 消えないと、チェックが飾りになってしまう。
/// </para>
/// </summary>
public interface ISourceFilter
{
    /// <summary>この予定を出すか。</summary>
    bool IncludesEvent(CalendarEvent value);

    /// <summary>このタスクを出すか。</summary>
    bool IncludesTask(TaskItem value);
}

/// <summary>
/// カレンダーごとの色。
/// <para>
/// 予定の色は<b>所属カレンダーで決まる</b>。1件ずつ選ばせない。Google Calendar と
/// 同じ考え方で、左パネルの色見本と画面上の帯が同じ色になる。
/// </para>
/// </summary>
public interface ICalendarPalette
{
    /// <summary>そのカレンダーの色（<c>#rrggbb</c>）。決まっていなければ null。</summary>
    string? ColorOf(string? calendarId);
}

/// <summary>表示するかどうかと、何色で出すか。</summary>
public interface ICalendarSources : ISourceFilter, ICalendarPalette;

/// <summary>すべて出し、色は既定に任せる。何も渡さなかったときに使う。</summary>
public sealed class DefaultCalendarSources : ICalendarSources
{
    public static readonly DefaultCalendarSources Instance = new();

    private DefaultCalendarSources() { }

    public bool IncludesEvent(CalendarEvent value) => true;

    public bool IncludesTask(TaskItem value) => true;

    /// <summary>null を返すと、表示側が既定のアクセント色を使う。</summary>
    public string? ColorOf(string? calendarId) => null;
}

/// <summary>左パネルに並べるカレンダー／タスクリスト1件。</summary>
public sealed class SourceListItemViewModel : ObservableObject
{
    private readonly Action<SourceListItemViewModel> _onToggled;
    private bool _isVisible = true;

    internal SourceListItemViewModel(string id, string name, string swatchColor,
        bool isVisible, Action<SourceListItemViewModel> onToggled)
    {
        Id = id;
        Name = name;
        SwatchColor = swatchColor;
        _isVisible = isVisible;
        _onToggled = onToggled;
    }

    /// <summary>カレンダー ID またはタスクリスト ID。</summary>
    public string Id { get; }

    /// <summary>
    /// 表示名。
    /// <para>
    /// 自分で作ったものは付けた名前、同期して取り込んだものは Google 側の名前。
    /// 名前を変えても ID は変わらないので、予定の所属は付け替えなくてよい。
    /// </para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 左に置く色見本（<c>#rrggbb</c>）。
    /// <para>取り込み済みなら Google の色、そうでなければ名前から決まる色。</para>
    /// </summary>
    public string SwatchColor { get; }

    /// <summary>チェックが入っているか。外すと月ビューと右ペインから消える。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Set(ref _isVisible, value)) _onToggled(this);
        }
    }
}

/// <summary>
/// 左パネルのカレンダー一覧とタスクリスト一覧。
/// <para>
/// 一覧は必ず <c>calendars</c> / <c>task_lists</c> の表から作る。以前は予定が持つ
/// 所属 ID から後付けで拾っていたが、それでは名前も色も変えられず、予定が1件も無い
/// うちは分類を先に作ることもできなかった。
/// </para>
/// </summary>
public sealed class SourceListsViewModel : ObservableObject, ICalendarSources
{
    private readonly CalendarWorkspace _workspace;

    // 取り込み前は表が空なので、隠している ID をここで覚える。
    // 取り込み後はデータベース側に持つので、終了しても残る（要件書 5.5）
    private readonly HashSet<string> _hiddenCalendars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hiddenTaskLists = new(StringComparer.Ordinal);

    private IReadOnlyList<SourceListItemViewModel> _calendars = [];
    private IReadOnlyList<SourceListItemViewModel> _taskLists = [];

    public SourceListsViewModel(CalendarWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Refresh();
    }

    /// <summary>チェックが変わった。ビューはこれを見て引き直す。</summary>
    public event EventHandler? VisibilityChanged;

    /// <summary>カレンダー一覧。</summary>
    public IReadOnlyList<SourceListItemViewModel> Calendars
    {
        get => _calendars;
        private set => Set(ref _calendars, value);
    }

    /// <summary>タスクリスト一覧。</summary>
    public IReadOnlyList<SourceListItemViewModel> TaskLists
    {
        get => _taskLists;
        private set => Set(ref _taskLists, value);
    }

    /// <summary>一覧を読み直す。チェックの状態は引き継ぐ。</summary>
    public void Refresh()
    {
        // 一覧は必ず表から作る。CalendarWorkspace が起動時に用意するので空にはならない
        Calendars = _workspace.Sources.Calendars()
            .Select(c => new SourceListItemViewModel(
                c.Id, c.DisplayName, c.BackgroundColor ?? CalendarPalette.ColorFor(c.Id),
                c.IsVisible, OnCalendarToggled))
            .ToArray();

        TaskLists = _workspace.Sources.TaskLists()
            .Select(t => new SourceListItemViewModel(
                t.Id, t.DisplayName, CalendarPalette.ColorFor(t.Id), t.IsVisible, OnTaskListToggled))
            .ToArray();

        RebuildHidden();
    }

    /// <summary>
    /// 所属が無い予定は常に出す。どこにも属していないだけで、消す理由にはならない。
    /// <para>
    /// ただし実働日データから起こしたマイルストーンは、<b>日付の行に別途出している</b>ので
    /// 予定の並びからは外す。外さないと同じ日に二度出る。所属カレンダーのチェックは
    /// 効くので、左パネルで消せば日付の行からも消える。
    /// </para>
    /// </summary>
    public bool IncludesEvent(CalendarEvent value) =>
        !IsMilestone(value) &&
        (value.CalendarId is not { Length: > 0 } id || !_hiddenCalendars.Contains(id));

    /// <summary>実働日データから起こしたマイルストーンか。</summary>
    public static bool IsMilestone(CalendarEvent value) =>
        string.Equals(value.Source, CalendarWorkspace.WorkingDaySource, StringComparison.Ordinal);

    public bool IncludesTask(TaskItem value) =>
        value.TaskListId is not { Length: > 0 } id || !_hiddenTaskLists.Contains(id);

    /// <summary>カレンダーの色。一覧に無い（＝所属なし）なら null で、既定の色に任せる。</summary>
    public string? ColorOf(string? calendarId) =>
        calendarId is { Length: > 0 } id
            ? _calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.SwatchColor
            : null;

    private void OnCalendarToggled(SourceListItemViewModel item)
    {
        // 終了しても残す（要件書 5.5）
        _workspace.Sources.SetCalendarVisible(item.Id, item.IsVisible);

        RebuildHidden();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTaskListToggled(SourceListItemViewModel item)
    {
        _workspace.Sources.SetTaskListVisible(item.Id, item.IsVisible);

        RebuildHidden();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>いま隠している ID を、一覧の状態から作り直す。絞り込みはこれを見る。</summary>
    private void RebuildHidden()
    {
        Sync(_hiddenCalendars, _calendars);
        Sync(_hiddenTaskLists, _taskLists);

        static void Sync(HashSet<string> hidden, IReadOnlyList<SourceListItemViewModel> items)
        {
            foreach (var item in items)
            {
                if (item.IsVisible) hidden.Remove(item.Id);
                else hidden.Add(item.Id);
            }
        }
    }
}
