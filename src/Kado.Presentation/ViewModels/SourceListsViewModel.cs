using Kado.Data.Models;
using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 表示するカレンダーとタスクリストの絞り込み。
/// <para>
/// 月ビューと右ペインはこれを見てから並べる。左パネルのチェックを外した分が
/// 消えないと、チェックが飾りになってしまう。
/// </para>
/// </summary>
public interface ISourceFilter
{
    /// <summary>この予定を、予定の並びに出すか。</summary>
    bool IncludesEvent(CalendarEvent value);

    /// <summary>
    /// この予定を、日付の行にマイルストーンとして出すか。
    /// <para>
    /// <see cref="IncludesEvent"/> とは排他。同じものを二度出さないため、
    /// こちらに出るものは予定の並びからは外れる。
    /// </para>
    /// </summary>
    bool IncludesMilestone(CalendarEvent value);

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

    /// <summary>
    /// 左パネルでの並び順。小さいほど上。
    /// <para>
    /// 時刻を持たない予定は時刻で並べられないので、代わりにこれで並べる。
    /// 知らないカレンダーは最後に回す。
    /// </para>
    /// </summary>
    int OrderOf(string? calendarId);
}

/// <summary>表示するかどうかと、何色で出すか。</summary>
public interface ICalendarSources : ISourceFilter, ICalendarPalette;

/// <summary>すべて出し、色は既定に任せる。何も渡さなかったときに使う。</summary>
public sealed class DefaultCalendarSources : ICalendarSources
{
    public static readonly DefaultCalendarSources Instance = new();

    private DefaultCalendarSources() { }

    public bool IncludesEvent(CalendarEvent value) => !CalendarWorkspace.IsMilestoneMark(value);

    public bool IncludesMilestone(CalendarEvent value) => CalendarWorkspace.IsMilestoneMark(value);

    public bool IncludesTask(TaskItem value) => true;

    /// <summary>null を返すと、表示側が既定のアクセント色を使う。</summary>
    public string? ColorOf(string? calendarId) => null;

    /// <summary>並び順を持たないので、すべて同じ扱いにする。</summary>
    public int OrderOf(string? calendarId) => 0;
}

/// <summary>並べ替えのとき、落とすと行のどちら側に入るか。</summary>
public enum DropHint
{
    /// <summary>いまは示さない。</summary>
    None,

    /// <summary>この行の上に入る。</summary>
    Above,

    /// <summary>この行の下に入る。</summary>
    Below,
}

/// <summary>左パネルに並べるカレンダー／タスクリスト1件。</summary>
public sealed class SourceListItemViewModel : ObservableObject
{
    private readonly Action<SourceListItemViewModel> _onToggled;
    private readonly Action<SourceListItemViewModel>? _onNotifyToggled;
    private bool _isVisible = true;
    private bool _notifies = true;
    private bool _isDefault;
    private DropHint _dropHint;

    internal SourceListItemViewModel(string id, string name, string swatchColor,
        bool isVisible, bool isGoogle, Action<SourceListItemViewModel> onToggled,
        bool notifies = true, Action<SourceListItemViewModel>? onNotifyToggled = null)
    {
        Id = id;
        Name = name;
        SwatchColor = swatchColor;
        _isVisible = isVisible;
        _notifies = notifies;
        IsGoogle = isGoogle;
        _onToggled = onToggled;
        _onNotifyToggled = onNotifyToggled;
    }

    /// <summary>
    /// このカレンダーの予定を既定で知らせるか。
    /// <para>ベルを押して切り替える。予定ごとの指定があれば、そちらが勝つ。</para>
    /// </summary>
    public bool Notifies
    {
        get => _notifies;
        set
        {
            if (Set(ref _notifies, value)) _onNotifyToggled?.Invoke(this);
        }
    }

    /// <summary>ベルを出すか。タスクリストは通知の対象にしていないので出さない。</summary>
    public bool HasNotifyToggle => _onNotifyToggled is not null;

    /// <summary>
    /// 新しい予定の入れ先か。
    /// <para>どこに入るのかが一覧で分かるよう、印を出す。</para>
    /// </summary>
    public bool IsDefault
    {
        get => _isDefault;
        internal set => Set(ref _isDefault, value);
    }

    /// <summary>
    /// Google 側にあるものか。
    /// <para>
    /// 左パネルはこれで二つに分ける。混ざっていると、消してよいのはどれか、
    /// 名前を変えたら相手にも伝わるのはどれかが読めない。
    /// </para>
    /// <para>
    /// 判断は<b>最後に Google から受け取った姿を持っているか</b>で行う。ID の形では
    /// 決めない。このアプリの印が付く前に作られたものが手元に残っているため。
    /// </para>
    /// </summary>
    public bool IsGoogle { get; }

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

    /// <summary>
    /// 並べ替えの最中、この行のどちら側に入るか。
    /// <para>
    /// 掴んだものを落としたときにどこへ入るのかが分からないと、何度もやり直すことになる。
    /// 行の上端・下端に線を出して示す。
    /// </para>
    /// </summary>
    public DropHint DropHint
    {
        get => _dropHint;
        set => Set(ref _dropHint, value);
    }

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

    // 日付の行に出すカレンダー。名前で見分ける（要望どおり）。このアプリで作ったものでも
    // Google から取り込んだものでも、名前が合えば同じ扱いにする
    private readonly HashSet<string> _milestoneCalendars = new(StringComparer.Ordinal);

    private IReadOnlyList<SourceListItemViewModel> _calendars = [];
    private IReadOnlyList<SourceListItemViewModel> _taskLists = [];
    private IReadOnlyList<SourceListItemViewModel> _localCalendars = [];
    private IReadOnlyList<SourceListItemViewModel> _googleCalendars = [];
    private IReadOnlyList<SourceListItemViewModel> _localTaskLists = [];
    private IReadOnlyList<SourceListItemViewModel> _googleTaskLists = [];

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

    /// <summary>このアプリの中だけにあるカレンダー。</summary>
    public IReadOnlyList<SourceListItemViewModel> LocalCalendars
    {
        get => _localCalendars;
        private set => Set(ref _localCalendars, value);
    }

    /// <summary>Google から取り込んだカレンダー。</summary>
    public IReadOnlyList<SourceListItemViewModel> GoogleCalendars
    {
        get => _googleCalendars;
        private set => Set(ref _googleCalendars, value);
    }

    /// <summary>このアプリの中だけにあるタスクリスト。</summary>
    public IReadOnlyList<SourceListItemViewModel> LocalTaskLists
    {
        get => _localTaskLists;
        private set => Set(ref _localTaskLists, value);
    }

    /// <summary>Google から取り込んだタスクリスト。</summary>
    public IReadOnlyList<SourceListItemViewModel> GoogleTaskLists
    {
        get => _googleTaskLists;
        private set => Set(ref _googleTaskLists, value);
    }

    /// <summary>
    /// カレンダーを「このアプリ」と「Google」に分けて見出しを出すか。
    /// <para>
    /// 両方にあるときだけ分ける。繋いでいないうちは全部がこのアプリのものなので、
    /// 見出しが1つだけ立っても読む人の助けにならない。
    /// </para>
    /// </summary>
    public bool ShowsCalendarGroups => _localCalendars.Count > 0 && _googleCalendars.Count > 0;

    /// <summary>タスクリストを分けて見出しを出すか。</summary>
    public bool ShowsTaskListGroups => _localTaskLists.Count > 0 && _googleTaskLists.Count > 0;

    /// <summary>
    /// 新しい予定の入れ先。
    /// <para>決まっていなければ一覧の先頭。「inaCalendar」は実働日データの入れ先なので避ける。</para>
    /// </summary>
    public SourceListItemViewModel? DefaultCalendar =>
        _calendars.FirstOrDefault(c => string.Equals(c.Id, DefaultCalendarId, StringComparison.Ordinal))
        ?? _calendars.FirstOrDefault(c => !string.Equals(
            c.Name, CalendarWorkspace.WorkingDayCalendarName, StringComparison.Ordinal))
        ?? _calendars.FirstOrDefault();

    /// <summary>設定で選ばれている入れ先。設定を持たない組み立て方では null。</summary>
    public string? DefaultCalendarId { get; set; }

    /// <summary>入れ先が変わったときに呼ばれる。設定に控えるのは持ち主の仕事。</summary>
    public event EventHandler<string>? DefaultCalendarChanged;

    /// <summary>入れ先を選び直す。</summary>
    public void SetDefaultCalendar(SourceListItemViewModel? item)
    {
        if (item is null) return;

        DefaultCalendarId = item.Id;
        MarkDefault();
        DefaultCalendarChanged?.Invoke(this, item.Id);
    }

    /// <summary>どれが入れ先かを行に反映する。</summary>
    private void MarkDefault()
    {
        var current = DefaultCalendar;

        foreach (var item in _calendars) item.IsDefault = ReferenceEquals(item, current);
    }

    /// <summary>
    /// 新しいタスクの入れ先。
    /// <para>
    /// 決まっていなければ、Google のタスクリストがあればその先頭。ローカルの
    /// <c>local:mytasks</c> は起動時に必ず先に作られるので、これを既定のまま
    /// 使うと、Google に繋いでいてもローカル固定になり、同期対象に届かない
    /// （項目2）。Google のリストが無ければローカルの先頭でしかたない。
    /// </para>
    /// </summary>
    public SourceListItemViewModel? DefaultTaskList =>
        _taskLists.FirstOrDefault(t => string.Equals(t.Id, DefaultTaskListId, StringComparison.Ordinal))
        ?? _googleTaskLists.FirstOrDefault()
        ?? _taskLists.FirstOrDefault();

    /// <summary>設定で選ばれている入れ先。設定を持たない組み立て方では null。</summary>
    public string? DefaultTaskListId { get; set; }

    /// <summary>入れ先が変わったときに呼ばれる。設定に控えるのは持ち主の仕事。</summary>
    public event EventHandler<string>? DefaultTaskListChanged;

    /// <summary>タスクの入れ先を選び直す。</summary>
    public void SetDefaultTaskList(SourceListItemViewModel? item)
    {
        if (item is null) return;

        DefaultTaskListId = item.Id;
        MarkDefaultTaskList();
        DefaultTaskListChanged?.Invoke(this, item.Id);
    }

    /// <summary>どれがタスクの入れ先かを行に反映する。</summary>
    private void MarkDefaultTaskList()
    {
        var current = DefaultTaskList;

        foreach (var item in _taskLists) item.IsDefault = ReferenceEquals(item, current);
    }

    /// <summary>ベルを押したとき。表のほうにも控える。</summary>
    private void OnCalendarNotifyToggled(SourceListItemViewModel item) =>
        _workspace.SetCalendarNotify(item.Id, item.Notifies);

    /// <summary>一覧を読み直す。チェックの状態は引き継ぐ。</summary>
    public void Refresh()
    {
        // 一覧は必ず表から作る。CalendarWorkspace が起動時に用意するので空にはならない
        Calendars = _workspace.Sources.Calendars()
            .Select(c => new SourceListItemViewModel(
                c.Id, c.DisplayName, c.BackgroundColor ?? CalendarPalette.ColorFor(c.Id),
                c.IsVisible, IsFromGoogle(c.GoogleRaw), OnCalendarToggled,
                c.NotifyDefault, OnCalendarNotifyToggled))
            .ToArray();

        TaskLists = _workspace.Sources.TaskLists()
            .Select(t => new SourceListItemViewModel(
                t.Id, t.DisplayName, CalendarPalette.ColorFor(t.Id), t.IsVisible,
                IsFromGoogle(t.GoogleRaw), OnTaskListToggled))
            .ToArray();

        LocalCalendars = _calendars.Where(c => !c.IsGoogle).ToArray();
        GoogleCalendars = _calendars.Where(c => c.IsGoogle).ToArray();
        LocalTaskLists = _taskLists.Where(t => !t.IsGoogle).ToArray();
        GoogleTaskLists = _taskLists.Where(t => t.IsGoogle).ToArray();

        // タスクの既定入れ先は Google の一覧（_googleTaskLists）を見て決めるので、
        // 上のグループ分けを済ませたあとで呼ぶこと。先に呼ぶと前回ぶんの古い一覧を見てしまう
        MarkDefault();
        MarkDefaultTaskList();

        Raise(nameof(ShowsCalendarGroups));
        Raise(nameof(ShowsTaskListGroups));

        RebuildHidden();
    }

    /// <summary>
    /// 日付の行に出す扱いか。
    /// <para>
    /// 実働日データから起こしたマイルストーンと、<b>「inaCalendar」という名前の
    /// カレンダーに入っている予定</b>。名前で見分けるので、このアプリで作ったものでも
    /// Google から取り込んだものでも同じ扱いになる。
    /// </para>
    /// <para>
    /// 名前で決めているので、カレンダーの名前を変えると外れる。逆に、別のカレンダーを
    /// この名前にすれば日付の行に出る。
    /// </para>
    /// </summary>
    public bool IsMilestoneEvent(CalendarEvent value) =>
        CalendarWorkspace.IsMilestoneMark(value) ||
        (value.CalendarId is { Length: > 0 } id && _milestoneCalendars.Contains(id));

    /// <summary>
    /// 所属が無い予定は常に出す。どこにも属していないだけで、消す理由にはならない。
    /// <para>
    /// ただし日付の行に出すものは、<b>予定の並びからは外す</b>。外さないと同じ日に二度出る。
    /// </para>
    /// </summary>
    public bool IncludesEvent(CalendarEvent value) =>
        !IsMilestoneEvent(value) &&
        (value.CalendarId is not { Length: > 0 } id || !_hiddenCalendars.Contains(id));

    /// <summary>
    /// 日付の行に出すか。
    /// <para>チェックを外したカレンダーのものは出さない。日付の行も左パネルに従う。</para>
    /// </summary>
    public bool IncludesMilestone(CalendarEvent value) =>
        IsMilestoneEvent(value) &&
        (value.CalendarId is not { Length: > 0 } id || !_hiddenCalendars.Contains(id));

    /// <summary>
    /// Google 側にあるものか。
    /// <para>
    /// 最後に受け取った姿を持っていれば、Google の一覧に載っていたということ。
    /// 取り込みは必ずこれを書くので、持っていないものは Google に無い。
    /// </para>
    /// </summary>
    public static bool IsFromGoogle(string? googleRaw) => googleRaw is { Length: > 0 };

    /// <inheritdoc cref="CalendarWorkspace.IsMilestoneMark"/>
    public static bool IsMilestone(CalendarEvent value) => CalendarWorkspace.IsMilestoneMark(value);

    public bool IncludesTask(TaskItem value) =>
        value.TaskListId is not { Length: > 0 } id || !_hiddenTaskLists.Contains(id);

    /// <summary>カレンダーの色。一覧に無い（＝所属なし）なら null で、既定の色に任せる。</summary>
    public string? ColorOf(string? calendarId) =>
        calendarId is { Length: > 0 } id
            ? _calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.SwatchColor
            : null;

    /// <summary>
    /// 左パネルでの並び順。
    /// <para>一覧はすでに並び順で読んであるので、その位置をそのまま使う。</para>
    /// </summary>
    public int OrderOf(string? calendarId)
    {
        if (calendarId is not { Length: > 0 }) return int.MaxValue;

        for (var i = 0; i < _calendars.Count; i++)
        {
            if (string.Equals(_calendars[i].Id, calendarId, StringComparison.Ordinal)) return i;
        }

        // 一覧に無いものは最後に回す。順番を決める手がかりが無い
        return int.MaxValue;
    }

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

    /// <summary>日付の行に出すカレンダーか。名前で見分ける。</summary>
    private static bool IsMilestoneCalendar(SourceListItemViewModel item) =>
        string.Equals(item.Name, CalendarWorkspace.WorkingDayCalendarName, StringComparison.Ordinal);

    /// <summary>いま隠している ID を、一覧の状態から作り直す。絞り込みはこれを見る。</summary>
    private void RebuildHidden()
    {
        Sync(_hiddenCalendars, _calendars);
        Sync(_hiddenTaskLists, _taskLists);

        _milestoneCalendars.Clear();
        foreach (var calendar in _calendars.Where(IsMilestoneCalendar)) _milestoneCalendars.Add(calendar.Id);

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
