using System.Globalization;
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
    private string _searchText = string.Empty;

    public MainViewModel(CalendarWorkspace workspace, DateOnly today, DayOfWeek weekStart = DayOfWeek.Sunday)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _today = today;

        SourceLists = new SourceListsViewModel(workspace);
        Month = new MonthViewModel(workspace, today, today, weekStart, SourceLists) { SelectedDate = today };
        SelectedDay = new SelectedDayViewModel(workspace, today, today, SourceLists);
        MiniCalendar = new MiniCalendarViewModel(workspace, today, today, weekStart) { SelectedDate = today };

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

        // 追加の画面はこのあとのフェーズで作る。押しても無反応だと壊れて見えるので、
        // いまは何が起きていないかを状態表示で伝える
        AddEventCommand = new RelayCommand(() => StatusMessage = "予定の追加画面はこのあとのフェーズで実装します");
        AddTaskCommand = new RelayCommand(() => StatusMessage = "タスクの追加画面はこのあとのフェーズで実装します");
        OpenWorkingDayCalculatorCommand =
            new RelayCommand(() => StatusMessage = "実働日計算の画面はこのあとのフェーズで実装します");

        _workspace.Undo.Changed += (_, _) => RaiseUndoState();
        _workspace.DataChanged += (_, _) => RefreshViews();

        // 左パネルのチェックを外したら、月ビューと右ペインからも消す
        SourceLists.VisibilityChanged += (_, _) =>
        {
            Month.Refresh();
            SelectedDay.Refresh();
        };
    }

    // ------------------------------------------------------------------
    // 子のビュー
    // ------------------------------------------------------------------

    /// <summary>月ビュー。</summary>
    public MonthViewModel Month { get; }

    /// <summary>右ペイン（選択日）。</summary>
    public SelectedDayViewModel SelectedDay { get; }

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
            if (Set(ref _currentView, value)) Raise(nameof(HintText));
        }
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
            MiniCalendar.Today = value;
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
            Raise();
        }
    }

    /// <summary>直近の操作の結果。元に戻したときなどに出す。</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value)) Raise(nameof(SyncStatusText));
        }
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

    /// <summary>実働日バッジに残りを出すか。今日を含む月だけ。</summary>
    public bool HasRemainingWorkingDays => Month.RemainingWorkingDays is not null;

    /// <summary>検索語。</summary>
    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value ?? string.Empty);
    }

    /// <summary>
    /// 同期の状態。Google 同期は Phase 5 なので、いまは未接続であることを出す。
    /// 状態表示が入っているときはそちらを優先する（置き場所を増やさない）。
    /// </summary>
    public string SyncStatusText => _statusMessage ?? "Google 未接続";

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
    public RelayCommand AddTaskCommand { get; }
    public RelayCommand OpenWorkingDayCalculatorCommand { get; }

    private void GoToPrevious()
    {
        Month.GoToPreviousMonth();
        MiniCalendar.GoTo(Month.Month);
        RaiseHeader();
    }

    private void GoToNext()
    {
        Month.GoToNextMonth();
        MiniCalendar.GoTo(Month.Month);
        RaiseHeader();
    }

    private void GoToToday()
    {
        Month.GoToToday();
        SelectedDay.Date = _today;
        MiniCalendar.GoTo(_today);
        MiniCalendar.SelectedDate = _today;
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
