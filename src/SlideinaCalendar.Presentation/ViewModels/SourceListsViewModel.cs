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

/// <summary>すべて出す絞り込み。絞り込みを渡さなかったときの既定。</summary>
public sealed class ShowAllFilter : ISourceFilter
{
    public static readonly ShowAllFilter Instance = new();

    private ShowAllFilter() { }

    public bool IncludesEvent(CalendarEvent value) => true;

    public bool IncludesTask(TaskItem value) => true;
}

/// <summary>左パネルに並べるカレンダー／タスクリスト1件。</summary>
public sealed class SourceListItemViewModel : ObservableObject
{
    private readonly Action<SourceListItemViewModel> _onToggled;
    private bool _isVisible = true;

    internal SourceListItemViewModel(string id, string swatchColor, Action<SourceListItemViewModel> onToggled)
    {
        Id = id;
        SwatchColor = swatchColor;
        _onToggled = onToggled;
    }

    /// <summary>カレンダー ID またはタスクリスト ID。</summary>
    public string Id { get; }

    /// <summary>
    /// 表示名。
    /// <para>
    /// Google 側の表示名はまだ取り込んでいないので ID をそのまま出す。
    /// 旧データからの移行では「仕事」「マイタスク」のような日本語が入っている。
    /// </para>
    /// </summary>
    public string Name => Id;

    /// <summary>左に置く色見本（<c>#rrggbb</c>）。名前から決まるので毎回同じ色になる。</summary>
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
public sealed class SourceListsViewModel : ObservableObject, ISourceFilter
{
    /// <summary>
    /// 色見本。モックの左パネルで使っている 5 色。
    /// <para>名前から決まる添字で選ぶので、同じ名前には常に同じ色が付く。</para>
    /// </summary>
    private static readonly string[] Palette =
        ["#27528f", "#2f7d5b", "#c2762b", "#8f5fa8", "#3b6ea8"];

    private readonly CalendarWorkspace _workspace;

    // 隠している ID。読み直しても選択が消えないよう、一覧とは別に持つ
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
        Calendars = Build(_workspace.Events.CalendarIds(), _hiddenCalendars);
        TaskLists = Build(_workspace.Tasks.TaskListIds(), _hiddenTaskLists);
    }

    /// <summary>所属が無い予定は常に出す。どこにも属していないだけで、消す理由にはならない。</summary>
    public bool IncludesEvent(CalendarEvent value) =>
        value.CalendarId is not { Length: > 0 } id || !_hiddenCalendars.Contains(id);

    public bool IncludesTask(TaskItem value) =>
        value.TaskListId is not { Length: > 0 } id || !_hiddenTaskLists.Contains(id);

    private IReadOnlyList<SourceListItemViewModel> Build(IReadOnlyList<string> ids, HashSet<string> hidden) =>
        ids.Select(id =>
        {
            var item = new SourceListItemViewModel(id, Palette[StableIndex(id)], OnToggled(hidden));
            if (hidden.Contains(id)) item.IsVisible = false;
            return item;
        }).ToArray();

    private Action<SourceListItemViewModel> OnToggled(HashSet<string> hidden) => item =>
    {
        if (item.IsVisible) hidden.Remove(item.Id);
        else hidden.Add(item.Id);

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    };

    /// <summary>名前から色を決める。string.GetHashCode は実行ごとに変わるので使えない。</summary>
    private static int StableIndex(string name)
    {
        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        return Math.Abs(hash % Palette.Length);
    }
}
