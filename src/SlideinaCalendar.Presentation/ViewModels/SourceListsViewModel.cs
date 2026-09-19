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
    /// 同期して一覧を取り込めば Google 側の名前になる。それまでは予定の所属 ID を
    /// そのまま出す（旧データからの移行では「仕事」のような日本語が入っている）。
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
/// カレンダー自体の表は持たないので、予定とタスクが持つ所属 ID から組み立てる
/// （<see cref="Data.Repositories.EventRepository.CalendarIds"/>）。
/// </para>
/// </summary>
public sealed class SourceListsViewModel : ObservableObject, ICalendarSources
{
    /// <summary>
    /// 色見本。モックの左パネルで使っている 5 色。
    /// <para>名前から決まる添字で選ぶので、同じ名前には常に同じ色が付く。</para>
    /// </summary>
    private static readonly string[] Palette =
        ["#27528f", "#2f7d5b", "#c2762b", "#8f5fa8", "#3b6ea8"];

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
        // 取り込み済みならそちらが正。名前も色も Google のものになる
        var calendars = _workspace.Sources.Calendars();
        Calendars = calendars.Count > 0
            ? calendars.Select(c => new SourceListItemViewModel(
                  c.Id, c.DisplayName, c.BackgroundColor ?? ColorFor(c.Id), c.IsVisible,
                  OnCalendarToggled)).ToArray()
            : Build(_workspace.Events.CalendarIds(), _hiddenCalendars, OnCalendarToggled);

        var taskLists = _workspace.Sources.TaskLists();
        TaskLists = taskLists.Count > 0
            ? taskLists.Select(t => new SourceListItemViewModel(
                  t.Id, t.DisplayName, ColorFor(t.Id), t.IsVisible, OnTaskListToggled)).ToArray()
            : Build(_workspace.Tasks.TaskListIds(), _hiddenTaskLists, OnTaskListToggled);

        RebuildHidden();
    }

    /// <summary>所属が無い予定は常に出す。どこにも属していないだけで、消す理由にはならない。</summary>
    public bool IncludesEvent(CalendarEvent value) =>
        value.CalendarId is not { Length: > 0 } id || !_hiddenCalendars.Contains(id);

    public bool IncludesTask(TaskItem value) =>
        value.TaskListId is not { Length: > 0 } id || !_hiddenTaskLists.Contains(id);

    /// <summary>カレンダーの色。一覧に無い（＝所属なし）なら null で、既定の色に任せる。</summary>
    public string? ColorOf(string? calendarId) =>
        calendarId is { Length: > 0 } id
            ? _calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.SwatchColor
            : null;

    private IReadOnlyList<SourceListItemViewModel> Build(
        IReadOnlyList<string> ids, HashSet<string> hidden, Action<SourceListItemViewModel> onToggled) =>
        ids.Select(id => new SourceListItemViewModel(
                id, id, ColorFor(id), !hidden.Contains(id), onToggled))
            .ToArray();

    private void OnCalendarToggled(SourceListItemViewModel item)
    {
        // 表があるならそちらへ。終了しても残す（要件書 5.5）
        if (!_workspace.Sources.SetCalendarVisible(item.Id, item.IsVisible)) Remember(_hiddenCalendars, item);

        RebuildHidden();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTaskListToggled(SourceListItemViewModel item)
    {
        if (!_workspace.Sources.SetTaskListVisible(item.Id, item.IsVisible)) Remember(_hiddenTaskLists, item);

        RebuildHidden();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Remember(HashSet<string> hidden, SourceListItemViewModel item)
    {
        if (item.IsVisible) hidden.Remove(item.Id);
        else hidden.Add(item.Id);
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

    /// <summary>
    /// 名前から色を決める。取り込み前と、色を持たないタスクリストで使う。
    /// <para>string.GetHashCode は実行ごとに変わるので使えない。同じ名前には常に同じ色。</para>
    /// </summary>
    private static string ColorFor(string name)
    {
        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        return Palette[Math.Abs(hash % Palette.Length)];
    }
}
