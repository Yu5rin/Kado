using System.Globalization;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>本体に出すビューの種類。</summary>
public enum CalendarView
{
    Month,
    Week,
    Day,

    /// <summary>年（ストリップ）。Phase 6 で実装する。</summary>
    Year,

    /// <summary>一覧（アジェンダ）。Phase 6 で実装する。</summary>
    Agenda,
}

/// <summary>
/// ウィンドウモードの本体。
/// <para>
/// 左のミニ月暦・中央の本体ビュー・右の選択日という3ペイン構成（要件書 5.2）。
/// 左パネルは折りたたむことができ、閉じても情報が欠けないよう実働日のサマリーは
/// ツールバー側に置く。
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly CalendarWorkspace _workspace;
    private readonly IEditorPresenter _editors;

    private CalendarView _currentView = CalendarView.Month;
    private bool _isSidePanelOpen = true;
    private DateOnly _today;
    private string? _statusMessage;
    private string _searchText = string.Empty;

    public MainViewModel(CalendarWorkspace workspace, DateOnly today, DayOfWeek weekStart = DayOfWeek.Sunday,
        IEditorPresenter? editors = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _editors = editors ?? NullEditorPresenter.Instance;
        _today = today;

        SourceLists = new SourceListsViewModel(workspace);
        Month = new MonthViewModel(workspace, today, today, weekStart, sources: SourceLists) { SelectedDate = today };
        SelectedDay = new SelectedDayViewModel(workspace, today, today, SourceLists);
        MiniCalendar = new MiniCalendarViewModel(workspace, today, today, weekStart) { SelectedDate = today };
        Week = new WeekViewModel(workspace, today, today, weekStart, SourceLists);
        Day = new DayViewModel(workspace, today, today, SourceLists);

        PreviousCommand = new RelayCommand(GoToPrevious);
        NextCommand = new RelayCommand(GoToNext);
        TodayCommand = new RelayCommand(GoToToday);
        ToggleSidePanelCommand = new RelayCommand(() => IsSidePanelOpen = !IsSidePanelOpen);
        UndoCommand = new RelayCommand(Undo, () => _workspace.Undo.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _workspace.Undo.CanRedo);
        SelectDateCommand = new RelayCommand<DateOnly?>(date => { if (date is { } d) SelectedDate = d; });
        SwitchViewCommand = new RelayCommand<CalendarView?>(view => { if (view is { } v) CurrentView = v; });

        MiniPreviousCommand = new RelayCommand(() => MiniCalendar.GoToPreviousMonth());
        MiniNextCommand = new RelayCommand(() => MiniCalendar.GoToNextMonth());

        AddEventCommand = new RelayCommand(AddEvent);
        AddEventOnCommand = new RelayCommand<DateOnly?>(date =>
        {
            // その日をダブルクリックして足すので、選択も移す
            if (date is { } d) SelectedDate = d;
            AddEvent();
        });
        AddTaskCommand = new RelayCommand(AddTask);
        EditEventCommand = new RelayCommand<DayEventViewModel?>(EditEvent);
        DeleteEventCommand = new RelayCommand<DayEventViewModel?>(DeleteEvent);
        EditTaskCommand = new RelayCommand<TaskListItemViewModel?>(EditTask);
        DeleteTaskCommand = new RelayCommand<TaskListItemViewModel?>(DeleteTask);
        ToggleTaskDoneCommand = new RelayCommand<TaskListItemViewModel?>(ToggleTaskDone);

        // 実働日計算の画面はこのあとのフェーズで作る。それまでは押せないことで示す
        OpenWorkingDayCalculatorCommand = new RelayCommand(() => { }, () => false);

        _workspace.Undo.Changed += (_, _) => RaiseUndoState();
        _workspace.DataChanged += (_, _) => RefreshViews();

        // 左パネルのチェックを外したら、月ビューと右ペインからも消す
        SourceLists.VisibilityChanged += (_, _) =>
        {
            Month.Refresh();
            SelectedDay.Refresh();
            Week.Refresh();
            Day.Refresh();
        };
    }

    // ------------------------------------------------------------------
    // 子のビュー
    // ------------------------------------------------------------------

    /// <summary>月ビュー。</summary>
    public MonthViewModel Month { get; }

    /// <summary>右ペイン（選択日）。</summary>
    public SelectedDayViewModel SelectedDay { get; }

    /// <summary>週ビュー。</summary>
    public WeekViewModel Week { get; }

    /// <summary>日ビュー。</summary>
    public DayViewModel Day { get; }

    /// <summary>左パネルのミニ月暦。中央とは独立して月を送れる。</summary>
    public MiniCalendarViewModel MiniCalendar { get; }

    /// <summary>左パネルのカレンダー一覧とタスクリスト一覧。</summary>
    public SourceListsViewModel SourceLists { get; }

    // ------------------------------------------------------------------
    // 状態
    // ------------------------------------------------------------------

    /// <summary>
    /// 本体に出しているビュー。
    /// <para>
    /// 月・週・日・年・一覧の5つ（要件書 5.1）。<b>半期ビューは設けない。</b>
    /// 上期・下期は年ビューのストリップで行のグルーピングとして示す。
    /// </para>
    /// </summary>
    public CalendarView CurrentView
    {
        get => _currentView;
        set
        {
            if (!Set(ref _currentView, value)) return;

            // 切り替えた先が別の日を見ていると、どこを見ているのか分からなくなる
            Week.GoTo(SelectedDate);
            Day.Date = SelectedDate;

            Raise(nameof(HintText), nameof(IsMonthView), nameof(IsWeekView), nameof(IsDayView));
        }
    }

    /// <summary>中央に出すビューの出し分け。</summary>
    public bool IsMonthView => _currentView == CalendarView.Month;

    public bool IsWeekView => _currentView == CalendarView.Week;

    public bool IsDayView => _currentView == CalendarView.Day;

    /// <summary>左サイドパネルを開いているか。終了時に保存して次回復元する。</summary>
    public bool IsSidePanelOpen
    {
        get => _isSidePanelOpen;
        set => Set(ref _isSidePanelOpen, value);
    }

    /// <summary>今日。日付が変わったら差し替える。</summary>
    public DateOnly Today
    {
        get => _today;
        set
        {
            if (!Set(ref _today, value)) return;

            Month.Today = value;
            SelectedDay.Today = value;
            MiniCalendar.Today = value;
            Week.Today = value;
            Day.Today = value;
        }
    }

    /// <summary>選択している日。</summary>
    public DateOnly SelectedDate
    {
        get => Month.SelectedDate ?? _today;
        set
        {
            if (Month.SelectedDate == value) return;

            Month.SelectedDate = value;
            SelectedDay.Date = value;
            MiniCalendar.SelectedDate = value;
            Week.GoTo(value);
            Day.Date = value;
            Raise();
        }
    }

    /// <summary>直近の操作の結果。元に戻したときなどに出す。</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    // ------------------------------------------------------------------
    // ツールバーの表示
    // ------------------------------------------------------------------

    /// <summary>「2026年9月」。</summary>
    public string Title => Month.Title;

    /// <summary>ツールバーの年。月より一段小さく、薄く出す。</summary>
    public string TitleYear => Month.Month.Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>ツールバーの月。「9月」。</summary>
    public string TitleMonth => Month.Month.ToString("M月", CultureInfo.InvariantCulture);

    /// <summary>実働日バッジを出せるか。データが無い月では数字を出さない。</summary>
    public bool HasWorkingDayData => Month.HasFullWorkingDayData;

    /// <summary>実働日バッジの実働日数。</summary>
    public string WorkingDayCountText =>
        Month.WorkingDayCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>実働日バッジの残り日数。今日を含まない月では空。</summary>
    public string RemainingWorkingDaysText =>
        Month.RemainingWorkingDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// 実働日バッジに残りを出すか。実働日データがあり、かつ今日を含む月だけ。
    /// <para>データが無いのに「残り 0 日」と出ると、実数だと思われる。</para>
    /// </summary>
    public bool HasRemainingWorkingDays => HasWorkingDayData && Month.RemainingWorkingDays is not null;

    /// <summary>検索語。</summary>
    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value ?? string.Empty);
    }

    /// <summary>
    /// 同期の状態。
    /// <para>
    /// ここに出すのは同期の状態だけ。操作の結果や未実装の断り書きを混ぜると、
    /// 同期できているのかどうかが読み取れなくなる。
    /// </para>
    /// </summary>
    public string SyncStatusText => IsSynced ? "同期済み" : "Google 未接続";

    /// <summary>同期できているか。丸印の色を変える。</summary>
    public bool IsSynced => false;

    /// <summary>本体ビューの下に出す凡例。ビューごとに変える。</summary>
    public string HintText => _currentView switch
    {
        CalendarView.Week => "終日レーンのタスクを時間帯へドラッグすると、作業時間としてブロックが置かれる",
        CalendarView.Day => "空き時間をドラッグすると予定を追加 ・ タスクをドロップすると作業時間を確保",
        CalendarView.Year => "1行が1か月 ・ 日付の上の横棒がマイルストーン ・ 右端は月の実働日数",
        CalendarView.Agenda => "予定とタスクを時系列で表示 ・ 予定のない休みはまとめて折りたたむ",
        _ => "色付きラベルは実働日データから取り込んだマイルストーン（編集不可） ・ ドラッグで期間選択 ・ Ctrl＋ドラッグで複製",
    };

    /// <summary>
    /// 「実働 20 ／ 残り 6」のサマリー。
    /// <para>
    /// 左パネルを閉じても情報が欠けないよう、ツールバー側に置く（要件書 5.2）。
    /// 実働日の通し番号を出す4か所のひとつでもある（要件書 4.3）。
    /// </para>
    /// </summary>
    public string WorkingDaySummary
    {
        get
        {
            if (!Month.HasFullWorkingDayData)
            {
                // 数字だけ出すと、登録範囲外なのに実数だと思われる
                return "実働日データ未登録";
            }

            return Month.RemainingWorkingDays is { } remaining
                ? $"今月の実働日 {Month.WorkingDayCount}日 ／ 残り {remaining}日"
                : $"今月の実働日 {Month.WorkingDayCount}日";
        }
    }

    /// <summary>元に戻せるか。</summary>
    public bool CanUndo => _workspace.Undo.CanUndo;

    /// <summary>やり直せるか。</summary>
    public bool CanRedo => _workspace.Undo.CanRedo;

    /// <summary>「予定の追加を元に戻す」のような説明。</summary>
    public string? UndoLabel =>
        _workspace.Undo.UndoDescription is { } d ? $"{d}を元に戻す" : "元に戻す";

    /// <summary>やり直しの説明。</summary>
    public string? RedoLabel =>
        _workspace.Undo.RedoDescription is { } d ? $"{d}をやり直す" : "やり直す";

    // ------------------------------------------------------------------
    // コマンド
    // ------------------------------------------------------------------

    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand TodayCommand { get; }
    public RelayCommand ToggleSidePanelCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand<DateOnly?> SelectDateCommand { get; }
    public RelayCommand<CalendarView?> SwitchViewCommand { get; }
    public RelayCommand MiniPreviousCommand { get; }
    public RelayCommand MiniNextCommand { get; }
    public RelayCommand AddEventCommand { get; }

    /// <summary>日を指定して予定を足す。月ビューのマスをダブルクリックしたとき。</summary>
    public RelayCommand<DateOnly?> AddEventOnCommand { get; }
    public RelayCommand AddTaskCommand { get; }
    public RelayCommand<DayEventViewModel?> EditEventCommand { get; }
    public RelayCommand<DayEventViewModel?> DeleteEventCommand { get; }
    public RelayCommand<TaskListItemViewModel?> EditTaskCommand { get; }
    public RelayCommand<TaskListItemViewModel?> DeleteTaskCommand { get; }
    public RelayCommand<TaskListItemViewModel?> ToggleTaskDoneCommand { get; }
    public RelayCommand OpenWorkingDayCalculatorCommand { get; }

    /// <summary>
    /// 前へ。動く幅は出しているビューで変わる（月・週・日）。
    /// <para>年と一覧はまだ無いので、月と同じ扱いにしておく。</para>
    /// </summary>
    private void GoToPrevious()
    {
        switch (_currentView)
        {
            case CalendarView.Week:
                Week.GoToPreviousWeek();
                SyncHeaderTo(Week.WeekStart);
                break;

            case CalendarView.Day:
                Day.GoToPreviousDay();
                SyncHeaderTo(Day.Date);
                break;

            default:
                Month.GoToPreviousMonth();
                SyncHeaderTo(Month.Month);
                break;
        }
    }

    private void GoToNext()
    {
        switch (_currentView)
        {
            case CalendarView.Week:
                Week.GoToNextWeek();
                SyncHeaderTo(Week.WeekStart);
                break;

            case CalendarView.Day:
                Day.GoToNextDay();
                SyncHeaderTo(Day.Date);
                break;

            default:
                Month.GoToNextMonth();
                SyncHeaderTo(Month.Month);
                break;
        }
    }

    private void GoToToday()
    {
        Month.GoToToday();
        SelectedDay.Date = _today;
        Week.GoToToday();
        Day.GoToToday();
        MiniCalendar.GoTo(_today);
        MiniCalendar.SelectedDate = _today;
        RaiseHeader();
        Raise(nameof(SelectedDate));
    }

    /// <summary>
    /// ツールバーの年月とミニ月暦を、いま見ている日に合わせる。
    /// <para>週や日を送って月をまたいだとき、見出しだけ前の月に残るのを防ぐ。</para>
    /// </summary>
    private void SyncHeaderTo(DateOnly anchor)
    {
        Month.GoTo(anchor);
        MiniCalendar.GoTo(anchor);
        RaiseHeader();
    }

    // ------------------------------------------------------------------
    // 予定とタスクの編集。どれも CalendarWorkspace を通すので Undo が効く
    // ------------------------------------------------------------------

    /// <summary>選択している日に予定を足す。</summary>
    private void AddEvent()
    {
        var editor = new EventEditorViewModel(SelectedDate, CalendarNames);
        if (!_editors.ShowEventEditor(editor)) return;

        _workspace.AddEvent(editor.ToModel());
        StatusMessage = "予定を追加しました";
    }

    private void EditEvent(DayEventViewModel? target)
    {
        if (target is null) return;

        // 表示用の複製ではなく保存されている内容を直す。繰り返しの展開を書き戻さないため
        if (_workspace.Events.Find(target.Id) is not { } stored) return;

        var editor = new EventEditorViewModel(stored, CalendarNames);
        if (!_editors.ShowEventEditor(editor)) return;

        StatusMessage = _workspace.UpdateEvent(editor.ToModel())
            ? "予定を変更しました"
            : "予定が見つかりませんでした";
    }

    private void DeleteEvent(DayEventViewModel? target)
    {
        if (target is null || !_editors.ConfirmDelete(target.Title)) return;

        StatusMessage = _workspace.DeleteEvent(target.Id)
            ? "予定を削除しました"
            : "予定が見つかりませんでした";
    }

    /// <summary>選択している日を期限にしてタスクを足す。</summary>
    private void AddTask()
    {
        var editor = new TaskEditorViewModel(SelectedDate, TaskListNames, _today);
        if (!_editors.ShowTaskEditor(editor)) return;

        _workspace.AddTask(editor.ToModel());
        StatusMessage = "タスクを追加しました";
    }

    private void EditTask(TaskListItemViewModel? target)
    {
        if (target is null) return;
        if (_workspace.Tasks.Find(target.Id) is not { } stored) return;

        var editor = new TaskEditorViewModel(stored, TaskListNames, _today);
        if (!_editors.ShowTaskEditor(editor)) return;

        StatusMessage = _workspace.UpdateTask(editor.ToModel())
            ? "タスクを変更しました"
            : "タスクが見つかりませんでした";
    }

    private void DeleteTask(TaskListItemViewModel? target)
    {
        if (target is null || !_editors.ConfirmDelete(target.Title)) return;

        StatusMessage = _workspace.DeleteTask(target.Id)
            ? "タスクを削除しました"
            : "タスクが見つかりませんでした";
    }

    /// <summary>チェックの入り切り。画面を開かずに切り替えられる。</summary>
    private void ToggleTaskDone(TaskListItemViewModel? target)
    {
        if (target is null || !_workspace.ToggleTaskDone(target.Id)) return;

        StatusMessage = target.IsDone ? "タスクの完了を取り消しました" : "タスクを完了にしました";
    }

    /// <summary>編集画面に出すカレンダーの候補。</summary>
    private IReadOnlyList<string> CalendarNames =>
        SourceLists.Calendars.Select(c => c.Id).ToArray();

    private IReadOnlyList<string> TaskListNames =>
        SourceLists.TaskLists.Select(t => t.Id).ToArray();

    private void Undo()
    {
        if (_workspace.UndoLast() is { } description) StatusMessage = $"{description}を元に戻しました";
    }

    private void Redo()
    {
        if (_workspace.RedoLast() is { } description) StatusMessage = $"{description}をやり直しました";
    }

    /// <summary>
    /// いまの時刻を伝える。週・日ビューの現在時刻の線が動く。
    /// <para>日付が変わっていたら「今日」も差し替える。起動しっぱなしで日をまたぐため。</para>
    /// </summary>
    public void UpdateNow(DateTime now)
    {
        var date = DateOnly.FromDateTime(now);
        if (date != _today) Today = date;

        var time = TimeOnly.FromDateTime(now);
        Week.UpdateNowLine(time);
        Day.UpdateNowLine(time);
    }

    /// <summary>データが変わったので表示を引き直す。</summary>
    private void RefreshViews()
    {
        Month.Refresh();
        SelectedDay.Refresh();
        Week.Refresh();
        Day.Refresh();
        MiniCalendar.Refresh();
        SourceLists.Refresh();
        RaiseHeader();
    }

    private void RaiseHeader() => Raise(
        nameof(Title), nameof(TitleYear), nameof(TitleMonth), nameof(WorkingDaySummary),
        nameof(HasWorkingDayData), nameof(WorkingDayCountText),
        nameof(RemainingWorkingDaysText), nameof(HasRemainingWorkingDays));

    private void RaiseUndoState()
    {
        Raise(nameof(CanUndo), nameof(CanRedo), nameof(UndoLabel), nameof(RedoLabel));
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
