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

    private CalendarView _currentView = CalendarView.Month;
    private bool _isSidePanelOpen = true;
    private DateOnly _today;
    private string? _statusMessage;

    public MainViewModel(CalendarWorkspace workspace, DateOnly today, DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _today = today;

        Month = new MonthViewModel(workspace, today, today, weekStart) { SelectedDate = today };
        SelectedDay = new SelectedDayViewModel(workspace, today, today);

        PreviousCommand = new RelayCommand(GoToPrevious);
        NextCommand = new RelayCommand(GoToNext);
        TodayCommand = new RelayCommand(GoToToday);
        ToggleSidePanelCommand = new RelayCommand(() => IsSidePanelOpen = !IsSidePanelOpen);
        UndoCommand = new RelayCommand(Undo, () => _workspace.Undo.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _workspace.Undo.CanRedo);
        SelectDateCommand = new RelayCommand<DateOnly?>(date => { if (date is { } d) SelectedDate = d; });
        SwitchViewCommand = new RelayCommand<CalendarView?>(view => { if (view is { } v) CurrentView = v; });

        _workspace.Undo.Changed += (_, _) => RaiseUndoState();
        _workspace.DataChanged += (_, _) => RefreshViews();
    }

    // ------------------------------------------------------------------
    // 子のビュー
    // ------------------------------------------------------------------

    /// <summary>月ビュー。</summary>
    public MonthViewModel Month { get; }

    /// <summary>右ペイン（選択日）。</summary>
    public SelectedDayViewModel SelectedDay { get; }

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
        set => Set(ref _currentView, value);
    }

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

    private void GoToPrevious()
    {
        Month.GoToPreviousMonth();
        RaiseHeader();
    }

    private void GoToNext()
    {
        Month.GoToNextMonth();
        RaiseHeader();
    }

    private void GoToToday()
    {
        Month.GoToToday();
        SelectedDay.Date = _today;
        RaiseHeader();
        Raise(nameof(SelectedDate));
    }

    private void Undo()
    {
        if (_workspace.UndoLast() is { } description) StatusMessage = $"{description}を元に戻しました";
    }

    private void Redo()
    {
        if (_workspace.RedoLast() is { } description) StatusMessage = $"{description}をやり直しました";
    }

    /// <summary>データが変わったので表示を引き直す。</summary>
    private void RefreshViews()
    {
        Month.Refresh();
        SelectedDay.Refresh();
        RaiseHeader();
    }

    private void RaiseHeader() => Raise(nameof(Title), nameof(WorkingDaySummary));

    private void RaiseUndoState()
    {
        Raise(nameof(CanUndo), nameof(CanRedo), nameof(UndoLabel), nameof(RedoLabel));
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
