using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.Input;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Google.Mapping;
using SlideinaCalendar.Google.OAuth;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.Notifications;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>本体に出すビューの種類。</summary>
public enum CalendarView
{
    Month,
    Week,
    Day,

    /// <summary>年（ストリップ／カレンダー）。年度単位（4月〜翌3月）。</summary>
    Year,

    /// <summary>一覧（アジェンダ）。予定のない日は畳んで流す。</summary>
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
    private readonly IFileDialogs _files;
    private readonly GoogleClientSecretsStore? _googleClient;

    private readonly AppSettings? _settings;
    private readonly IStartupRegistration _startup;
    private readonly WorkdayFeedClient _feed = new();

    /// <summary>予定の前と朝のまとめを知らせる。設定を渡されていなければ持たない。</summary>
    private readonly ReminderService? _reminders;

    /// <summary>知らせる口。設定画面の「試しに知らせる」でも使う。</summary>
    private readonly INotifier _notifier;

    private CalendarView _currentView = CalendarView.Month;
    private DayOfWeek _weekStart;
    private bool _isSidePanelOpen = true;
    private bool _isMainViewOpen = true;
    private bool _isDetailPaneOpen = true;
    private readonly TimeProvider _clock;
    private DateOnly _today;
    private string? _statusMessage;
    private string _searchText = string.Empty;
    private IReadOnlyList<SearchResultViewModel> _searchResults = [];
    private string? _searchMessage;
    private string _quickText = string.Empty;
    private double _sidePanelWidth;
    private double _detailPaneWidth;
    private bool _isBusy;

    public MainViewModel(CalendarWorkspace workspace, DateOnly today, DayOfWeek weekStart = DayOfWeek.Sunday,
        IEditorPresenter? editors = null, IFileDialogs? files = null,
        GoogleClientSecretsStore? googleClient = null,
        Sync.IGoogleSync? google = null,
        TimeProvider? clock = null,
        AppSettings? settings = null,
        IStartupRegistration? startup = null,
        INotifier? notifier = null,
        DockPlacement? shell = null)
    {
        _clock = clock ?? TimeProvider.System;
        _googleClient = googleClient;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _editors = editors ?? NullEditorPresenter.Instance;
        _files = files ?? NullFileDialogs.Instance;
        _today = today;

        _sidePanelWidth = ReadWidth(SidePanelWidthKey, DefaultSidePanelWidth, MinSidePanelWidth, MaxSidePanelWidth);
        _detailPaneWidth = ReadWidth(DetailPaneWidthKey, DefaultDetailPaneWidth, MinDetailPaneWidth, MaxDetailPaneWidth);

        // 設定を渡されていれば、そちらが週の始まりを決める。渡されないのはテストと、
        // 設定を持たない画面。その場合は引数のままにする
        _settings = settings;
        _startup = startup ?? NullStartupRegistration.Instance;
        _notifier = notifier ?? NullNotifier.Instance;
        _weekStart = settings?.WeekStart ?? weekStart;

        Shell = new ShellViewModel(shell ?? DockPlacement.Unknown);
        SourceLists = new SourceListsViewModel(workspace);
        SelectedDay = new SelectedDayViewModel(workspace, today, today, SourceLists);
        BuildViews(today, today);

        if (settings is not null)
        {
            workspace.CountInCalendarDays = settings.CountInCalendarDays;

            SourceLists.DefaultCalendarId = settings.DefaultCalendarId;
            SourceLists.DefaultCalendarChanged += (_, id) => settings.DefaultCalendarId = id;

            SourceLists.DefaultTaskListId = settings.DefaultTaskListId;
            SourceLists.DefaultTaskListChanged += (_, id) => settings.DefaultTaskListId = id;
            _reminders = new ReminderService(workspace, settings, _notifier);

            // 週の始まりや表示時間帯が変わったら、その形でビューを組み直す
            settings.Changed += (_, _) =>
            {
                _weekStart = settings.WeekStart;
                workspace.CountInCalendarDays = settings.CountInCalendarDays;
                RebuildViews();
            };

            CurrentView = settings.StartupView;

            // 配信元が決まっていれば、1日1回だけ取りに行く
            if (settings is { FeedAuto: true, FeedUrl.Length: > 0 } && settings.FeedCheckedOn != today)
            {
                _ = FetchFeedAsync(quiet: true);
            }
        }

        PreviousCommand = new RelayCommand(GoToPrevious);
        NextCommand = new RelayCommand(GoToNext);
        TodayCommand = new RelayCommand(GoToToday);
        ToggleSidePanelCommand = new RelayCommand(() => IsSidePanelOpen = !IsSidePanelOpen);
        ToggleMainViewCommand = new RelayCommand(() => IsMainViewOpen = !IsMainViewOpen);
        ToggleDetailPaneCommand = new RelayCommand(() => IsDetailPaneOpen = !IsDetailPaneOpen);
        ToggleSlimPanelCommand = new RelayCommand(() => IsSlimPanelOpen = !IsSlimPanelOpen);
        SlimPreviousCommand = new RelayCommand(() => SlimGoTo(SlimMonth.Month.AddMonths(-1)));
        SlimNextCommand = new RelayCommand(() => SlimGoTo(SlimMonth.Month.AddMonths(1)));
        ShowShortcutsCommand = new RelayCommand(() => _editors.ShowShortcuts());
        Shell.PropertyChanged += (_, args) =>
        {
            // ウィンドウ側から FitTo を呼んでいるが、取りこぼすと詰め方が
            // 古いまま残る。幅が変わったことはここでも受けておく
            if (args.PropertyName == nameof(ShellViewModel.LayoutWidth)) FitTo(Shell.LayoutWidth);
        };

        // 居かたが変わったら、そのときに出していたパネルの組へ入れ替える
        Shell.ModeChanged += (_, mode) =>
        {
            var atEdge = mode != ShellMode.Window;

            if (atEdge == _atEdge) return;

            SavePanes();
            _atEdge = atEdge;
            LoadPanes();
        };

        _atEdge = Shell.Mode != ShellMode.Window;
        LoadPanes();

        OpenSearchCommand = new RelayCommand(() =>
        {
            _searchOpen = true;
            Raise(nameof(ShowsSearchBox), nameof(UsesCompactSearch));
        });
        UndoCommand = new RelayCommand(Undo, () => _workspace.Undo.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _workspace.Undo.CanRedo);
        SelectDateCommand = new RelayCommand<DateOnly?>(date => { if (date is { } d) SelectedDate = d; });
        SwitchViewCommand = new RelayCommand<object?>(view =>
        {
            // XAML からは名前（文字列）で渡ってくる
            if (view is CalendarView chosen) CurrentView = chosen;
            else if (view is string name && Enum.TryParse<CalendarView>(name, out var parsed)) CurrentView = parsed;
        });
        ShowMonthOfCommand = new RelayCommand<DateOnly?>(date => ShowOn(date, CalendarView.Month));
        ShowDayOfCommand = new RelayCommand<DateOnly?>(date => ShowOn(date, CalendarView.Day));
        ZoomInCommand = new RelayCommand(() => Zoom(1));
        ZoomOutCommand = new RelayCommand(() => Zoom(-1));

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
        EditChipCommand = new RelayCommand<EventChipViewModel?>(chip => EditEventBy(chip?.Id));
        DeleteChipCommand = new RelayCommand<EventChipViewModel?>(chip => DeleteEventBy(chip?.Id));
        EditTaskChipCommand = new RelayCommand<TaskItem?>(task => EditTaskBy(task?.Id));
        DeleteTaskChipCommand = new RelayCommand<TaskItem?>(task => DeleteTaskBy(task?.Id));
        ToggleTaskChipDoneCommand = new RelayCommand<TaskItem?>(ToggleTaskChipDone);
        EditBlockCommand = new RelayCommand<TimeBlockViewModel?>(EditBlock);
        EditMilestoneCommand = new RelayCommand<MilestoneViewModel?>(m => EditEventBy(m?.Id));
        DeleteMilestoneCommand = new RelayCommand<MilestoneViewModel?>(m => DeleteEventBy(m?.Id));
        DeleteBlockCommand = new RelayCommand<TimeBlockViewModel?>(
            block => DeleteEventBy(block?.IsWorkBlock == false ? block.Id : null));
        DeleteEventCommand = new RelayCommand<DayEventViewModel?>(DeleteEvent);
        EditTaskCommand = new RelayCommand<TaskListItemViewModel?>(EditTask);
        DeleteTaskCommand = new RelayCommand<TaskListItemViewModel?>(DeleteTask);
        ToggleTaskDoneCommand = new RelayCommand<TaskListItemViewModel?>(ToggleTaskDone);

        // 実働日計算の画面はこのあとのフェーズで作る。それまでは押せないことで示す
        OpenWorkingDayCalculatorCommand = new RelayCommand(ShowWorkdayCalculator);

        // 設定を持たない組み立て方（テストなど）では開けない。
        //
        // sync 以降は、⚙メニューから設定画面へ移した項目の配線。このコマンド自体は
        // ここで一度だけ作るが、中のラムダはボタンを押した時点（＝コンストラクタを
        // 抜けたあと）に評価されるので、まだ代入していないプロパティ（Sync や
        // 下の各コマンド）を先に参照しても構わない
        OpenSettingsCommand = new RelayCommand(
            () => _editors.ShowSettings(new SettingsViewModel(
                _settings!, _startup, _notifier, SourceLists.Calendars, Sync,
                ImportWorkingDaysCommand, ImportLegacyBackupCommand, FetchWorkingDayFeedCommand,
                ExportWorkingDayFeedCommand, RemoveDuplicatesCommand, BackupCommand, RestoreCommand,
                ImportGoogleClientCommand, CheckForUpdateCommand)),
            () => _settings is not null);

        AddCalendarCommand = new RelayCommand(() => AddSource(isTaskList: false));
        AddTaskListCommand = new RelayCommand(() => AddSource(isTaskList: true));
        EditSourceCommand = new RelayCommand<SourceListItemViewModel?>(EditSource);
        DeleteSourceCommand = new RelayCommand<SourceListItemViewModel?>(DeleteSource, CanDeleteSource);

        QuickCommand = new RelayCommand(CommitQuick, () => CanCommitQuick);

        SetDefaultCalendarCommand = new RelayCommand<SourceListItemViewModel?>(item =>
        {
            SourceLists.SetDefaultCalendar(item);
            if (item is not null) StatusMessage = $"新しい予定は「{item.Name}」に入ります";
        });

        SetDefaultTaskListCommand = new RelayCommand<SourceListItemViewModel?>(item =>
        {
            SourceLists.SetDefaultTaskList(item);
            if (item is not null) StatusMessage = $"新しいタスクは「{item.Name}」に入ります";
        });

        RemoveDuplicatesCommand = new RelayCommand(RemoveDuplicates);

        FetchWorkingDayFeedCommand = new AsyncRelayCommand(
            () => FetchFeedAsync(quiet: false),
            () => _settings is { FeedUrl.Length: > 0 },
            ex => StatusMessage = $"配信元から取り込めませんでした（{ex.Message}）");

        ExportWorkingDayFeedCommand = new RelayCommand(ExportFeed);

        BackupCommand = new RelayCommand(Backup);
        RestoreCommand = new RelayCommand(Restore, () => RestoreBackup is not null);

        ImportWorkingDaysCommand = new RelayCommand(ImportWorkingDays);
        ImportLegacyBackupCommand = new RelayCommand(ImportLegacyBackup);
        ImportGoogleClientCommand = new RelayCommand(ImportGoogleClient, () => _googleClient is not null);

        CheckForUpdateCommand = new AsyncRelayCommand(
            () => CheckForUpdate?.Invoke() ?? Task.CompletedTask,
            () => CheckForUpdate is not null,
            ex => StatusMessage = $"更新を確かめられませんでした（{ex.Message}）");

        Sync = new SyncViewModel(google);

        // 同期で中身が変わる。所属カレンダーも増えるので一覧ごと引き直す
        Sync.Synced += (_, _) =>
        {
            _workspace.EnsureSources();

            // 「inaCalendar」の印も同期で増減する。実働日を組み立て直さないと、
            // 他の端末で取り込んだ分がこちらでは「未登録」のままになる。
            // この中から DataChanged が飛ぶので、画面はそれで引き直される
            _workspace.ReloadWorkingDays();
        };

        // 右上の表示は Sync が持つ。こちらは伝えるだけ
        Sync.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SyncViewModel.StatusText) or nameof(SyncViewModel.State))
            {
                Raise(nameof(SyncStatusText), nameof(IsSynced));
            }
        };

        // 中止ボタンで止めたときだけ、下のステータス行に断りを出す（項目8）。
        // 失敗ではないので、Sync 側の赤い表示（StatusText）は使わない
        Sync.Cancelled += (_, _) => StatusMessage = "同期を中止しました";

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
    public MonthViewModel Month { get; private set; }

    /// <summary>
    /// スリムパネルのひと月。
    /// <para>中央とは別に持つ。中央が週や日を出していても、こちらは月のまま。</para>
    /// </summary>
    public MonthViewModel SlimMonth { get; private set; }

    /// <summary>スリムパネルの見出し。「2026」。</summary>
    public string SlimTitleYear =>
        SlimMonth.Month.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>スリムパネルの見出し。「9月」。</summary>
    public string SlimTitleMonth =>
        SlimMonth.Month.ToString("M月", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>右ペイン（選択日）。</summary>
    public SelectedDayViewModel SelectedDay { get; }

    /// <summary>週ビュー。</summary>
    public WeekViewModel Week { get; private set; }

    /// <summary>日ビュー。</summary>
    public DayViewModel Day { get; private set; }

    /// <summary>
    /// 画面での居かた（ウィンドウ／オーバーレイ／ドック）。
    /// <para>実際に画面へ効かせるのはアプリ側。ここが持つのは「どうしたいか」だけ。</para>
    /// </summary>
    public ShellViewModel Shell { get; }

    /// <summary>年ビュー（ストリップ／カレンダー）。年度単位。</summary>
    public YearViewModel Year { get; private set; }

    /// <summary>一覧ビュー。予定のない日は畳んで流す。</summary>
    public AgendaViewModel Agenda { get; private set; }

    /// <summary>左パネルのミニ月暦。中央とは独立して月を送れる。</summary>
    public MiniCalendarViewModel MiniCalendar { get; private set; }

    /// <summary>左パネルのカレンダー一覧とタスクリスト一覧。</summary>
    public SourceListsViewModel SourceLists { get; }

    /// <summary>右上の同期表示と、その操作。</summary>
    public SyncViewModel Sync { get; }

    /// <summary>
    /// 新しい版を確かめる。
    /// <para>
    /// 中身は Windows 側の仕事（実行ファイルの入れ替え）なので、ここでは呼び口だけ持つ。
    /// 入っていなければメニューを押せなくする。
    /// </para>
    /// </summary>
    public Func<Task>? CheckForUpdate { get; set; }

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

            // 先に日を合わせる。年ビューは年度が変われば自分で組み直すので、
            // 順序を逆にすると同じ組み立てを2回やることになる
            FocusOn(SelectedDate);

            // 出す番になった。まだ溜まっていれば、ここで済ませる
            RefreshHeavyIfShown();

            Raise(nameof(IsMonthView), nameof(IsWeekView), nameof(IsDayView),
                nameof(IsYearView), nameof(IsAgendaView), nameof(ShowsMonthHeader));
            RaiseHeader();
        }
    }

    /// <summary>中央に出すビューの出し分け。</summary>
    public bool IsMonthView => _currentView == CalendarView.Month;

    public bool IsWeekView => _currentView == CalendarView.Week;

    public bool IsDayView => _currentView == CalendarView.Day;

    public bool IsYearView => _currentView == CalendarView.Year;

    public bool IsAgendaView => _currentView == CalendarView.Agenda;

    /// <summary>
    /// ツールバーの「◀ ▶」で年月を送るビューか。
    /// <para>一覧は期間を流すので、見出しの年月と食い違わないよう別に扱う。</para>
    /// </summary>
    public bool ShowsMonthHeader => _currentView is not CalendarView.Year;

    /// <summary>左サイドパネルを開いているか。終了時に保存して次回復元する。</summary>
    public bool IsSidePanelOpen
    {
        get => _isSidePanelOpen;
        set
        {
            if (!Set(ref _isSidePanelOpen, value)) return;

            EnsureSomethingShows(nameof(IsSidePanelOpen));
            SavePanes();
            Raise(nameof(ShowsCalendarTools), nameof(ShowsViewSwitcher),
                nameof(ShowsDayNav), nameof(ShowsToolbarDate), nameof(ShowsToolbarNav),
                nameof(ShowsSlimToday));
        }
    }

    /// <summary>
    /// スリムパネルを出しているか。
    /// <para>
    /// ひと月の並びと、選んだ日の予定・タスクだけを細く置く4つ目のパネル。
    /// <b>幅では切り替えない。</b>出すかどうかは、ほかのパネルと同じく手で選ぶ。
    /// 狭いからといって別の形へ化けると、同じ操作を探し直すことになる。
    /// </para>
    /// </summary>
    public bool IsSlimPanelOpen
    {
        get => _isSlimPanelOpen;
        set
        {
            if (!Set(ref _isSlimPanelOpen, value)) return;

            EnsureSomethingShows(nameof(IsSlimPanelOpen));
            SavePanes();
            Raise(nameof(ShowsSlimToday), nameof(ShowsToolbarNav));
        }
    }

    /// <summary>
    /// スリムパネルに「今日」を出すか。
    /// <para>
    /// ほかのパネルが出ていればツールバーに「今日」がある。スリムパネルだけの
    /// ときは、そこが唯一の戻り口になる。
    /// </para>
    /// </summary>
    public bool ShowsSlimToday =>
        _isSlimPanelOpen && !_isSidePanelOpen && !_isMainViewOpen && !_isDetailPaneOpen;

    private bool _isSlimPanelOpen;

    public RelayCommand ToggleSlimPanelCommand { get; private set; } = null!;

    /// <summary>スリムパネルの月を前へ。中央とは別に送る。</summary>
    public RelayCommand SlimPreviousCommand { get; private set; } = null!;

    /// <summary>スリムパネルの月を次へ。</summary>
    public RelayCommand SlimNextCommand { get; private set; } = null!;

    private void SlimGoTo(DateOnly month)
    {
        SlimMonth.GoTo(month);
        Raise(nameof(SlimTitleYear), nameof(SlimTitleMonth));
    }

    /// <summary>
    /// 中央のカレンダーを出しているか。
    /// <para>
    /// 画面端に細く留めているときは、カレンダー本体を畳んで予定だけを見たいことがある。
    /// </para>
    /// </summary>
    public bool IsMainViewOpen
    {
        get => _isMainViewOpen;
        set
        {
            if (!Set(ref _isMainViewOpen, value)) return;

            EnsureSomethingShows(nameof(IsMainViewOpen));
            SavePanes();
            Raise(nameof(ShowsCalendarTools), nameof(ShowsViewSwitcher),
                nameof(ShowsDayNav), nameof(ShowsToolbarDate), nameof(ShowsToolbarNav),
                nameof(ShowsSlimToday));
            RaiseHeader();
        }
    }

    /// <summary>右の選択日パネルを出しているか。</summary>
    public bool IsDetailPaneOpen
    {
        get => _isDetailPaneOpen;
        set
        {
            if (!Set(ref _isDetailPaneOpen, value)) return;

            EnsureSomethingShows(nameof(IsDetailPaneOpen));
            SavePanes();
            Raise(nameof(ShowsCalendarTools), nameof(ShowsViewSwitcher),
                nameof(ShowsDayNav), nameof(ShowsToolbarDate), nameof(ShowsToolbarNav),
                nameof(ShowsSlimToday));
        }
    }

    /// <summary>
    /// ツールバーにカレンダーの操作を出すか。
    /// <para>
    /// 中央を畳んでいるとき、ビューの切り替えや「◀ ▶」は効かせどころが無い。
    /// 出したままだと、押しても何も起きないボタンが並ぶ。
    /// </para>
    /// </summary>
    public bool ShowsCalendarTools => _isMainViewOpen;

    /// <summary>
    /// ツールバーに年月の見出しを出すか。
    /// <para>
    /// <b>決めるのは中央を出しているかどうか。</b>中央を畳んでいるなら、見ているのは
    /// 選んだ1日で、年月の見出しは右ペインの日付欄やミニ月暦と同じことを二度言う。
    /// </para>
    /// </summary>
    public bool ShowsToolbarDate => _isMainViewOpen;

    /// <summary>
    /// 日送りを右ペインの日付欄に置くか。
    /// <para>
    /// 送りは日付のすぐ隣にあるほうが近い。ただし置けるのは日付欄があるとき、
    /// つまり右ペインを出しているときだけ。
    /// </para>
    /// </summary>
    public bool ShowsDayNav => !_isMainViewOpen && _isDetailPaneOpen;

    /// <summary>
    /// ツールバーに「◀ ▶」を置くか。
    /// <para>
    /// 日付欄へ移したときは置かない。スリムパネルだけのときも、そちらの見出しに
    /// 月の送りがあるので置かない。同じものが2か所にあると、どちらが効くのか迷う。
    /// </para>
    /// </summary>
    public bool ShowsToolbarNav => !ShowsDayNav && !ShowsSlimToday;

    // ------------------------------------------------------------------
    // 幅に合わせた詰め方
    //
    // 細い帯として使うので、入りきらないものは順に落とす。何を残すかは
    // 使う人に決めてもらった。残すのは、年月の見出し・「◀ ▶」・今日・
    // 検索（虫めがねに畳む）・≡、そして戻り口になるピンと▥と設定。
    // ------------------------------------------------------------------

    /// <summary>実働・残りのバッジを出す下限。同じ数字は右ペインの日付欄にも出る。</summary>
    public const double WorkdayBadgeFloor = 1000;

    /// <summary>同期の状態（●同期済み）を出す下限。</summary>
    public const double SyncStatusFloor = 880;

    /// <summary>検索の入力欄をそのまま出す下限。これを切ると虫めがねのボタンに畳む。</summary>
    public const double SearchBoxFloor = 820;

    /// <summary>ビュー切り替え（一覧・年・月・週・日）を出す下限。</summary>
    public const double ViewSwitcherFloor = 700;

    /// <summary>「今日」を出す下限。ここまで細いと、置く場所が無い。</summary>
    public const double TodayButtonFloor = 380;

    /// <summary>
    /// スリムパネルで、カレンダーに割く高さの割合。
    /// <para>仕切りをつまんで変えたぶんを覚える。</para>
    /// </summary>
    public double SlimCalendarShare
    {
        get => _settings?.SlimCalendarShare ?? AppSettings.DefaultSlimShare;
        set
        {
            if (_settings is not { } settings) return;

            settings.SlimCalendarShare = value;
        }
    }

    /// <summary>スリムパネルの既定の幅。</summary>
    public const double DefaultSlimPanelWidth = 264;

    /// <summary>スリムパネルの下げ止まり。マスが正方形で読める幅。</summary>
    public const double MinSlimPanelWidth = 200;

    /// <summary>スリムパネルの上げ止まり。これ以上広げるなら、ふつうのパネルを使う。</summary>
    public const double MaxSlimPanelWidth = 420;

    /// <summary>
    /// 中央のカレンダーの下げ止まり。
    /// <para>帯として使うときはここまで詰める。月ビューの7列がぎりぎり読める幅。</para>
    /// </summary>
    public const double MinMainViewWidth = 260;

    /// <summary>ツールバーの詰め方を決める幅。ウィンドウの見た目の幅。</summary>
    private double Room => Shell.LayoutWidth;

    /// <summary>まだ幅が分からない（起動直後など）。そのときは出したままにする。</summary>
    private bool RoomUnknown => double.IsNaN(Room) || Room <= 0;

    public bool ShowsWorkdayBadges => RoomUnknown || Room >= WorkdayBadgeFloor;

    public bool ShowsSyncStatus => RoomUnknown || Room >= SyncStatusFloor;

    /// <summary>検索を虫めがねのボタンに畳むか。押すと入力欄が開く。</summary>
    public bool UsesCompactSearch => !RoomUnknown && Room < SearchBoxFloor;

    /// <summary>
    /// 検索の入力欄を出すか。
    /// <para>
    /// 畳んでいるあいだは虫めがねのボタンだけを置き、押されたら入力欄を開く。
    /// 細い帯では他のものを押しのけて出るが、探しているあいだだけのこと。
    /// </para>
    /// </summary>
    public bool ShowsSearchBox => !UsesCompactSearch || _searchOpen;

    private bool _searchOpen;

    /// <summary>虫めがねを押したとき。入力欄を開く。</summary>
    public RelayCommand OpenSearchCommand { get; private set; } = null!;

    public bool ShowsViewSwitcher => ShowsCalendarTools && (RoomUnknown || Room >= ViewSwitcherFloor);

    /// <summary>
    /// 「今日」をアイコンだけに畳むか。
    /// <para>
    /// 帯として細く使う場面ほど「今日へ戻る」が要るので、幅が無くても消さない
    /// （項目10）。消す代わりに、380px を切ったらアイコンだけの姿にする。
    /// </para>
    /// </summary>
    public bool UsesCompactTodayButton => !RoomUnknown && Room < TodayButtonFloor;

    /// <summary>年月の見出しに取っておく幅。細いときは詰める。</summary>
    public double TitleRoom => UsesCompactTodayButton ? 44 : 118;

    /// <summary>
    /// 幅に合わせてツールバーの中身を詰める。
    /// <para>
    /// <b>パネルは勝手に畳まない。</b>狭いからと消していたが、出しておきたくて
    /// 出しているものが幅の都合で消えるのは筋が悪い。入りきらないぶんは切れる
    /// だけにして、何を出すかは手で決めてもらう。
    /// </para>
    /// </summary>
    public void FitTo(double width)
    {
        Raise(nameof(ShowsWorkdayBadges), nameof(ShowsSyncStatus), nameof(UsesCompactSearch),
            nameof(ShowsSearchBox), nameof(ShowsViewSwitcher), nameof(UsesCompactTodayButton),
            nameof(TitleRoom));
    }

    // ------------------------------------------------------------------
    // 出しているパネルの組み合わせ
    //
    // ウィンドウのときと、画面端に寄せているときとで別に覚える。広い
    // ウィンドウでは3つとも出し、細い帯では予定だけ、という使い分けが
    // ふつうなので、行き来のたびに直すのは手間になる
    // ------------------------------------------------------------------

    /// <summary>どのパネルを出しているか。</summary>
    private readonly record struct PaneSet(bool Side, bool Main, bool Detail, bool Slim)
    {
        /// <summary>ウィンドウのときの既定。3ペインを開く。</summary>
        public static PaneSet Windowed { get; } = new(true, true, true, false);

        /// <summary>画面端に寄せたときの既定。細いので、選んだ日の予定だけ。</summary>
        public static PaneSet AtEdge { get; } = new(false, false, true, false);

        public override string ToString() => $"{Bit(Side)}{Bit(Main)}{Bit(Detail)}{Bit(Slim)}";

        private static char Bit(bool on) => on ? '1' : '0';

        public static PaneSet Parse(string? text, PaneSet fallback) =>
            text is { Length: 4 }
                ? new(text[0] == '1', text[1] == '1', text[2] == '1', text[3] == '1')
                : fallback;
    }

    /// <summary>いま画面端に寄せている（スライド・固定）か。組の切り替えに使う。</summary>
    private bool _atEdge;

    /// <summary>組を読み書きしている最中。そのあいだは控え直さない。</summary>
    private bool _swappingPanes;

    private PaneSet CurrentPanes() =>
        new(_isSidePanelOpen, _isMainViewOpen, _isDetailPaneOpen, _isSlimPanelOpen);

    /// <summary>いまの組を控える。</summary>
    private void SavePanes()
    {
        if (_settings is not { } settings || _swappingPanes) return;

        var text = CurrentPanes().ToString();

        if (_atEdge) settings.EdgePanes = text;
        else settings.WindowPanes = text;
    }

    /// <summary>控えてある組に戻す。</summary>
    private void LoadPanes()
    {
        var set = _atEdge
            ? PaneSet.Parse(_settings?.EdgePanes, PaneSet.AtEdge)
            : PaneSet.Parse(_settings?.WindowPanes, PaneSet.Windowed);

        _swappingPanes = true;

        try
        {
            IsSidePanelOpen = set.Side;
            IsMainViewOpen = set.Main;
            IsDetailPaneOpen = set.Detail;
            IsSlimPanelOpen = set.Slim;
        }
        finally
        {
            _swappingPanes = false;
        }

        // 読み込んだ組が空っぽだったときだけ、ここで1つ戻す
        EnsureSomethingShows(nameof(IsMainViewOpen));
    }

    /// <summary>
    /// 3つとも畳もうとしたら、最後の1つは残す。
    /// <para>全部消すと、窓だけがそこにあって何もできなくなる。</para>
    /// </summary>
    private void EnsureSomethingShows(string justChanged)
    {
        // 組を入れ替えている最中は見ない。閉じてから開くので、途中で
        // 「3つとも畳んだ」状態を通る。そこで中央を戻すと、読み込んだ組に
        // 余計なパネルが混ざる（スリムだけにしたのに中央が出てくる）
        if (_swappingPanes) return;

        if (_isSidePanelOpen || _isMainViewOpen || _isDetailPaneOpen || _isSlimPanelOpen) return;

        // いま閉じたものではなく、中央を戻す。何を見る画面なのかが分かる
        if (justChanged == nameof(IsMainViewOpen)) Set(ref _isDetailPaneOpen, true, nameof(IsDetailPaneOpen));
        else Set(ref _isMainViewOpen, true, nameof(IsMainViewOpen));
    }

    // ------------------------------------------------------------------
    // 3ペインの幅
    //
    // 両端は手で決めた幅のまま置く。ウィンドウを広げたぶんは中央が受け取る。
    // 左パネルの一覧も右ペインの予定も、幅が増えて嬉しいのは中央のほう
    // ------------------------------------------------------------------

    private const string SidePanelWidthKey = "ui.side_panel_width";
    private const string DetailPaneWidthKey = "ui.detail_pane_width";

    /// <summary>左パネルの幅の既定値。モックの .side と同じ。</summary>
    public const double DefaultSidePanelWidth = 216;

    /// <summary>右ペインの幅の既定値。モックの .detail と同じ。</summary>
    public const double DefaultDetailPaneWidth = 296;

    /// <summary>左パネルの幅の下限。これより狭いとカレンダー名が読めない。</summary>
    public const double MinSidePanelWidth = 160;

    /// <summary>左パネルの幅の上限。</summary>
    public const double MaxSidePanelWidth = 480;

    /// <summary>右ペインの幅の下限。</summary>
    public const double MinDetailPaneWidth = 200;

    /// <summary>右ペインの幅の上限。</summary>
    public const double MaxDetailPaneWidth = 640;

    /// <summary>左パネルの幅。手で変えた値をそのまま次回に持ち越す。</summary>
    public double SidePanelWidth
    {
        get => _sidePanelWidth;
        set => SetWidth(ref _sidePanelWidth, value, MinSidePanelWidth, MaxSidePanelWidth,
            SidePanelWidthKey, nameof(SidePanelWidth));
    }

    /// <summary>右ペインの幅。手で変えた値をそのまま次回に持ち越す。</summary>
    public double DetailPaneWidth
    {
        get => _detailPaneWidth;
        set => SetWidth(ref _detailPaneWidth, value, MinDetailPaneWidth, MaxDetailPaneWidth,
            DetailPaneWidthKey, nameof(DetailPaneWidth));
    }

    /// <summary>幅を収まる範囲に丸めてから覚える。範囲外の値が保存に残らないようにする。</summary>
    private void SetWidth(
        ref double field, double value, double min, double max, string key, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;

        var rounded = Math.Clamp(Math.Round(value), min, max);
        if (Math.Abs(rounded - field) < 0.5) return;

        field = rounded;
        _workspace.Settings.Set(key, rounded.ToString(CultureInfo.InvariantCulture));
        Raise(name);
    }

    /// <summary>保存されている幅を読む。読めなければ既定値。</summary>
    private double ReadWidth(string key, double fallback, double min, double max) =>
        double.TryParse(_workspace.Settings.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var saved)
            ? Math.Clamp(saved, min, max)
            : fallback;

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
            Week.SelectedDate = value;
            Day.Date = value;

            // 年と一覧にも伝える。押した日がそこでも光っていないと、
            // ビューを切り替えたときにどこを見ていたのか分からなくなる
            Year.SelectedDate = value;
            Agenda.SelectedDate = value;

            // 前後の月のマスを押したら、スリムパネルもその月へ移る。
            // 選んだ日が見えない月を出したままでは、どこを選んだのか分からない
            SlimMonth.SelectedDate = value;

            if (value.Year != SlimMonth.Month.Year || value.Month != SlimMonth.Month.Month)
            {
                SlimMonth.GoTo(value);
                Raise(nameof(SlimTitleYear), nameof(SlimTitleMonth));
            }

            // 中央を畳んでいるときは、見出しが選んだ日そのものになっている
            if (!_isMainViewOpen) RaiseHeader();

            Raise();
        }
    }

    /// <summary>
    /// 直近の操作の結果。元に戻したときなどに出す。
    /// <para>
    /// 出しっぱなしにはしない。画面側（<c>MainWindow</c>）が数秒後に
    /// <see cref="ClearStatusMessage"/> を呼んで消す。ここ自身はタイマーを持たない
    /// （WPF に依存しない作りを保つため。<c>DispatcherTimer</c> は App 側の役目）。
    /// </para>
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    /// <summary>
    /// 出しているステータスを消す。
    /// <para>画面側が一定時間後に呼ぶ。すでに次の内容に差し替わっていれば、何もしない。</para>
    /// </summary>
    public void ClearStatusMessage() => StatusMessage = null;

    /// <summary>
    /// 取り込み・復元など、重い処理が走っている間。
    /// <para>
    /// 画面側（<c>MainWindow</c>）はこれを見て待機カーソルに変える（項目5）。
    /// このクラスは WPF に依存しない作りを保つので、カーソルそのものはここでは持たない。
    /// </para>
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    // ------------------------------------------------------------------
    // ツールバーの表示
    // ------------------------------------------------------------------

    /// <summary>「2026年9月」。</summary>
    public string Title => Month.Title;

    /// <summary>
    /// ツールバーの年。月より一段小さく、薄く出す。
    /// <para>年ビューでは年度を出す。「◀ ▶」が年度を送るのに、見出しが月のままだと食い違う。</para>
    /// </summary>
    public string TitleYear => this switch
    {
        // 中央を畳んでいるときは、出ている日そのものを見出しにする
        { _isMainViewOpen: false } => SelectedDate.Year.ToString(CultureInfo.InvariantCulture),
        { _currentView: CalendarView.Year } => Year.FiscalYear.ToString(CultureInfo.InvariantCulture),
        _ => Month.Month.Year.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>ツールバーの月。「9月」。年ビューでは「年度」。中央を畳んでいれば「9月21日」。</summary>
    public string TitleMonth => this switch
    {
        { _isMainViewOpen: false } => SelectedDate.ToString("M月d日", CultureInfo.InvariantCulture),
        { _currentView: CalendarView.Year } => "年度",
        _ => Month.Month.ToString("M月", CultureInfo.InvariantCulture),
    };

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
        set
        {
            if (!Set(ref _searchText, value ?? string.Empty)) return;

            RunSearch();
        }
    }

    // ------------------------------------------------------------------
    // クイック入力
    //
    // 「明日15時 打合せ @会議室A」と1行打てば入る。編集画面を開いて欄を
    // 埋めるより速い。読めない言い回しは黙って一部だけ入れず、断って止める
    // ------------------------------------------------------------------

    /// <summary>
    /// クイック入力の1行。
    /// <para>
    /// 日付を書かなければ<b>選んでいる日（<see cref="SelectedDate"/>）</b>に入る。
    /// ただし「明日」「来週の火曜」「3日後」のような相対語は<b>今日から見た日</b>のまま
    /// で、選んでいる日は見ない。月を送って眺めている最中に「明日」と打って、思っていた
    /// のと違う日に入るのを防ぐため。
    /// </para>
    /// </summary>
    public string QuickText
    {
        get => _quickText;
        set
        {
            if (!Set(ref _quickText, value ?? string.Empty)) return;

            Raise(nameof(QuickPreview), nameof(CanCommitQuick));
            QuickCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 打った1行をどう読んだか。入れる前に見せる。
    /// <para>読めなければ、なぜ入れられないのかを出す。</para>
    /// </summary>
    public string? QuickPreview
    {
        get
        {
            if (_quickText.Trim().Length == 0) return null;

            var entry = QuickParser.Parse(_quickText, _today, SelectedDate);

            if (entry.UnsupportedWord is { } word) return $"「{word}」はここでは読めません。予定の画面から入れてください";
            if (entry.HasDateError) return "その日は暦にありません";
            if (entry.HasTimeError) return "その時刻はありません";
            if (entry.Title.Length == 0) return "予定の名前がありません";

            var when = entry.Start is { } start
                ? $"{entry.Date:M/d}（{Weekday(entry.Date)}） {start:HH\\:mm}"
                    + (entry.End is { } end ? $"–{end:HH\\:mm}" : string.Empty)
                : $"{entry.Date:M/d}（{Weekday(entry.Date)}） 終日";

            return entry.Location is { Length: > 0 } place
                ? $"{when} ・ {entry.Title} ・ {place}"
                : $"{when} ・ {entry.Title}";
        }
    }

    /// <summary>そのまま入れられるか。</summary>
    public bool CanCommitQuick => QuickParser.Parse(_quickText, _today, SelectedDate).CanCommit;

    /// <summary>1行から予定を入れる。</summary>
    public RelayCommand QuickCommand { get; }

    private void CommitQuick()
    {
        var entry = QuickParser.Parse(_quickText, _today, SelectedDate);
        if (!entry.CanCommit) return;

        _workspace.AddEvent(new CalendarEvent
        {
            Id = Guid.NewGuid().ToString("N")[..15],
            Title = entry.Title,
            Date = entry.Date,
            StartTime = entry.Start,

            // 終わりを書いていなければ1時間。時刻を書いていなければ終日のまま
            EndTime = entry.End ?? (entry.Start is { } start ? start.AddHours(1) : null),
            Location = entry.Location,
            CalendarId = QuickCalendarId,
            UpdatedAt = DateTimeOffset.Now,
        });

        SelectedDate = entry.Date;
        QuickText = string.Empty;
        StatusMessage = $"「{entry.Title}」を追加しました";
    }

    private static string Weekday(DateOnly date) => "日月火水木金土"[(int)date.DayOfWeek].ToString();

    /// <summary>
    /// 新しい予定を入れる先のカレンダー。
    /// <para>
    /// 左の一覧で選ばれているもの。選んでいなければ一覧の先頭で、「inaCalendar」は
    /// 避ける。入れてしまうと、次の取り込みで消える場所に置くことになる。
    /// </para>
    /// </summary>
    private string? QuickCalendarId => SourceLists.DefaultCalendar?.Id;

    /// <summary>検索で見つかったもの。多くても50件までにする。</summary>
    public IReadOnlyList<SearchResultViewModel> SearchResults
    {
        get => _searchResults;
        private set => Set(ref _searchResults, value);
    }

    /// <summary>検索の結果を出しているか。</summary>
    public bool IsSearching => _searchText.Trim().Length > 0;

    /// <summary>見つからなかったときなどの断り書き。見つかっていれば null。</summary>
    public string? SearchMessage
    {
        get => _searchMessage;
        private set => Set(ref _searchMessage, value);
    }

    /// <summary>探すのをやめる。欄を空にして結果も消す。</summary>
    public void ClearSearch()
    {
        SearchText = string.Empty;

        // 虫めがねに畳んでいたぶんは、探し終えたら元の1つのボタンに戻す
        if (!_searchOpen) return;

        _searchOpen = false;
        Raise(nameof(ShowsSearchBox), nameof(UsesCompactSearch));
    }

    /// <summary>
    /// 見つかったものを開く。その日へ移って、検索は閉じる。
    /// </summary>
    public void OpenSearchResult(SearchResultViewModel? found)
    {
        if (found is null) return;

        SelectedDate = found.Date;
        ClearSearch();

        if (found.IsTask) EditTaskBy(found.Id);
        else EditEventBy(found.Id);
    }

    /// <summary>
    /// 題・場所・メモから探す。
    /// <para>
    /// 並びは<b>今日に近い順</b>。単純な日付順だと、件数を絞ったときに古いものだけが
    /// 残る。出すときは日付の昇順に並べ直す（一覧として読みやすいため）。
    /// </para>
    /// </summary>
    private void RunSearch()
    {
        Raise(nameof(IsSearching));

        var text = _searchText.Trim();
        if (text.Length == 0)
        {
            SearchResults = [];
            SearchMessage = null;
            return;
        }

        var events = _workspace.Events.All()
            .Where(e => !CalendarWorkspace.IsMilestoneMark(e))
            .Where(e => Hits(text, e.Title, e.Location, e.Note))
            .Select(SearchResultViewModel.Of);

        // 期限の無いタスクも対象にする（項目1）。期限が無ければ選んでいる日で代用して並べる
        var tasks = _workspace.Tasks.All()
            .Where(t => Hits(text, t.Title, null, t.Note))
            .Select(t => SearchResultViewModel.Of(t, t.Due ?? SelectedDate));

        var found = events.Concat(tasks)
            .OrderBy(r => Math.Abs(r.Date.DayNumber - _today.DayNumber))
            .ThenBy(r => r.Date)
            .Take(SearchLimit)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ToArray();

        SearchResults = found;
        SearchMessage = found.Length == 0 ? "見つかりませんでした" : null;
    }

    /// <summary>題・場所・メモのどれかに含まれるか。大文字小文字は区別しない。</summary>
    private static bool Hits(string text, string? title, string? location, string? note) =>
        Contains(title, text) || Contains(location, text) || Contains(note, text);

    private static bool Contains(string? value, string text) =>
        value is { Length: > 0 } && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>出す件数の上限。これ以上出しても目で追えない。</summary>
    private const int SearchLimit = 50;

    /// <summary>
    /// 同期の状態。
    /// <para>
    /// ここに出すのは同期の状態だけ。操作の結果や未実装の断り書きを混ぜると、
    /// 同期できているのかどうかが読み取れなくなる。
    /// </para>
    /// </summary>
    public string SyncStatusText => Sync.StatusText;

    /// <summary>同期できているか。丸印の色を変える。</summary>
    public bool IsSynced => Sync.IsConnected;

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
    /// <summary>
    /// ビューを切り替える。
    /// <para>
    /// 名前（文字列）でも受ける。ツールバーのボタンは <c>IsChecked</c> の双方向
    /// バインドをやめてこのコマンドで切り替えている。RadioButton は仲間が選ばれた
    /// ときに <c>IsChecked</c> を直に書き換えるので、<b>そこで双方向のバインドが
    /// 外れてしまい、以後どれだけ切り替えてもボタンが光らなくなる</b>（WPF の癖）。
    /// </para>
    /// </summary>
    public RelayCommand<object?> SwitchViewCommand { get; }
    public RelayCommand MiniPreviousCommand { get; }
    public RelayCommand MiniNextCommand { get; }
    public RelayCommand AddEventCommand { get; }

    /// <summary>日を指定して予定を足す。月ビューのマスをダブルクリックしたとき。</summary>
    public RelayCommand<DateOnly?> AddEventOnCommand { get; }
    public RelayCommand AddTaskCommand { get; }
    public RelayCommand<DayEventViewModel?> EditEventCommand { get; }

    /// <summary>月ビューのマスに並ぶ予定を開く。右ペインの行とは別の型なので分けてある。</summary>
    public RelayCommand<EventChipViewModel?> EditChipCommand { get; }

    /// <summary>月ビューのマスに並ぶ予定を消す。</summary>
    public RelayCommand<EventChipViewModel?> DeleteChipCommand { get; }

    /// <summary>月ビューのマスに並ぶタスクを開く。</summary>
    public RelayCommand<TaskItem?> EditTaskChipCommand { get; }

    /// <summary>月ビューのマスに並ぶタスクを消す。</summary>
    public RelayCommand<TaskItem?> DeleteTaskChipCommand { get; }

    /// <summary>
    /// 月・週・日ビューのタスクチップの完了を切り替える。
    /// <para>
    /// これらのチップは <see cref="TaskItem"/>（保存されている生の形）を直に持つので、
    /// 右ペイン・スリムパネル用の <see cref="ToggleTaskDoneCommand"/>
    /// （<see cref="TaskListItemViewModel"/> 用）とは型が違う。
    /// </para>
    /// </summary>
    public RelayCommand<TaskItem?> ToggleTaskChipDoneCommand { get; }

    /// <summary>日付の行のラベルを開く。実働日データから起こした予定も直せる。</summary>
    public RelayCommand<MilestoneViewModel?> EditMilestoneCommand { get; }

    /// <summary>日付の行のラベルを消す。</summary>
    public RelayCommand<MilestoneViewModel?> DeleteMilestoneCommand { get; }

    /// <summary>週ビュー・日ビューの時間軸に置かれた1件を開く。</summary>
    public RelayCommand<TimeBlockViewModel?> EditBlockCommand { get; }

    /// <summary>週ビュー・日ビューの時間軸に置かれた予定を消す。作業時間ブロックは対象外。</summary>
    public RelayCommand<TimeBlockViewModel?> DeleteBlockCommand { get; }
    public RelayCommand<DayEventViewModel?> DeleteEventCommand { get; }
    public RelayCommand<TaskListItemViewModel?> EditTaskCommand { get; }
    public RelayCommand<TaskListItemViewModel?> DeleteTaskCommand { get; }
    public RelayCommand<TaskListItemViewModel?> ToggleTaskDoneCommand { get; }
    /// <summary>その日を選んで月ビューへ。</summary>
    public RelayCommand<DateOnly?> ShowMonthOfCommand { get; }

    /// <summary>その日を選んで日ビューへ。</summary>
    public RelayCommand<DateOnly?> ShowDayOfCommand { get; }

    /// <summary>ひとつ細かいビューへ（一覧 → 年 → 月 → 週 → 日）。</summary>
    public RelayCommand ZoomInCommand { get; }

    /// <summary>ひとつ粗いビューへ。</summary>
    public RelayCommand ZoomOutCommand { get; }

    /// <summary>実働日計算パネルを開く。常設はしない（要件書 4.5）。</summary>
    public RelayCommand OpenWorkingDayCalculatorCommand { get; }

    /// <summary>設定画面を開く。</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>ショートカットの一覧を開く。押せることを知らないと使われない。</summary>
    public RelayCommand ShowShortcutsCommand { get; }

    /// <summary>中央のカレンダーを出す・畳む。</summary>
    public RelayCommand ToggleMainViewCommand { get; }

    /// <summary>右の選択日パネルを出す・畳む。</summary>
    public RelayCommand ToggleDetailPaneCommand { get; }

    /// <summary>新しい予定の入れ先にする。</summary>
    public RelayCommand<SourceListItemViewModel?> SetDefaultCalendarCommand { get; }

    /// <summary>新しいタスクの入れ先にする（項目2）。</summary>
    public RelayCommand<SourceListItemViewModel?> SetDefaultTaskListCommand { get; }

    /// <summary>同じ内容の予定を1つにまとめる。</summary>
    public RelayCommand RemoveDuplicatesCommand { get; }

    /// <summary>配信元から実働日データを取りに行く。</summary>
    public AsyncRelayCommand FetchWorkingDayFeedCommand { get; }

    /// <summary>いまの実働日データを配信用に書き出す。</summary>
    public RelayCommand ExportWorkingDayFeedCommand { get; }

    /// <summary>いまの内容をファイルに書き出す。</summary>
    public RelayCommand BackupCommand { get; }

    /// <summary>書き出したファイルで置き換える。</summary>
    public RelayCommand RestoreCommand { get; }

    /// <summary>
    /// いまの内容をファイルに書き出す口。App 側が入れる。
    /// <para>データベースそのものを扱うので、接続を持っている側でないと書けない。</para>
    /// </summary>
    public Action<string>? SaveBackup { get; set; }

    /// <summary>
    /// ファイルで置き換える口。App 側が入れる。
    /// <para>
    /// 置き換えは接続を閉じてから行い、そのあとアプリを立ち上げ直す。
    /// 開いたまま差し替えると壊れる。
    /// </para>
    /// </summary>
    public Action<string>? RestoreBackup { get; set; }

    /// <summary>カレンダーを作る。</summary>
    public RelayCommand AddCalendarCommand { get; }

    /// <summary>タスクリストを作る。</summary>
    public RelayCommand AddTaskListCommand { get; }

    /// <summary>カレンダーまたはタスクリストの名前と色を変える。</summary>
    public RelayCommand<SourceListItemViewModel?> EditSourceCommand { get; }

    /// <summary>カレンダーまたはタスクリストを消す。</summary>
    public RelayCommand<SourceListItemViewModel?> DeleteSourceCommand { get; }

    /// <summary>新しい版があるか確かめる。</summary>
    public AsyncRelayCommand CheckForUpdateCommand { get; }

    /// <summary>配布の実働日ファイル（Excel）を取り込む。</summary>
    public RelayCommand ImportWorkingDaysCommand { get; }

    /// <summary>旧 inaCalendar のバックアップ（JSON）を取り込む。</summary>
    public RelayCommand ImportLegacyBackupCommand { get; }

    /// <summary>Google Cloud Console から落としたクライアント設定を取り込む。</summary>
    public RelayCommand ImportGoogleClientCommand { get; }

    /// <summary>Google 連携の下ごしらえが済んでいるか。</summary>
    public bool HasGoogleClient => _googleClient?.Exists ?? false;

    /// <summary>
    /// 前へ。動く幅は出しているビューで変わる（月・週・日）。
    /// <para>年と一覧はまだ無いので、月と同じ扱いにしておく。</para>
    /// </summary>
    private void GoToPrevious()
    {
        // 中央を畳んでいると、出ているのは選んだ日の予定だけ。月を送っても
        // 手応えが無いので、日を送る
        if (!_isMainViewOpen)
        {
            SelectedDate = SelectedDate.AddDays(-1);
            return;
        }

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

            case CalendarView.Year:
                Year.GoToPreviousYear();
                FollowInto(SameDayInFiscalYear());
                RaiseHeader();
                break;

            // 一覧は全部出しているので、送るのは選んでいる日のほう。
            // そこまで画面が動く
            case CalendarView.Agenda:
                SelectedDate = SelectedDate.AddMonths(-1);
                Agenda.GoTo(SelectedDate);
                break;

            default:
                Month.GoToPreviousMonth();
                SyncHeaderTo(Month.Month);
                break;
        }
    }

    private void GoToNext()
    {
        if (!_isMainViewOpen)
        {
            SelectedDate = SelectedDate.AddDays(1);
            return;
        }

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

            case CalendarView.Year:
                Year.GoToNextYear();
                FollowInto(SameDayInFiscalYear());
                RaiseHeader();
                break;

            case CalendarView.Agenda:
                SelectedDate = SelectedDate.AddMonths(1);
                Agenda.GoTo(SelectedDate);
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
        Year.GoToToday();
        Agenda.GoToToday();
        MiniCalendar.GoTo(_today);
        MiniCalendar.SelectedDate = _today;
        SlimMonth.GoTo(_today);
        SlimMonth.SelectedDate = _today;
        RaiseHeader();
        Raise(nameof(SelectedDate), nameof(SlimTitleYear), nameof(SlimTitleMonth));
    }

    /// <summary>
    /// ツールバーの年月とミニ月暦を、いま見ている日に合わせる。
    /// <para>週や日を送って月をまたいだとき、見出しだけ前の月に残るのを防ぐ。</para>
    /// </summary>
    private void SyncHeaderTo(DateOnly anchor)
    {
        // 送った先へ、選んでいる日も連れていく。置いていくと、中央は 9/18 を
        // 出しているのに右ペインは 9/22 のまま、ということになる
        FollowInto(anchor);

        Month.GoTo(anchor);
        MiniCalendar.GoTo(anchor);
        RaiseHeader();
    }

    /// <summary>
    /// 送った先に、選んでいる日を置き直す。
    /// <para>
    /// 日にちや曜日はなるべく保つ。月を送って 9/18 から 10/18 へ、週を送って
    /// 金曜から翌週の金曜へ、という動き方のほうが、行き先を見失わない。
    /// </para>
    /// </summary>
    private void FollowInto(DateOnly anchor)
    {
        var selected = SelectedDate;

        SelectedDate = _currentView switch
        {
            // 日ビューはその日そのもの
            CalendarView.Day => anchor,

            // 週は曜日を保つ。anchor は週の頭
            CalendarView.Week => anchor.AddDays(
                (((int)selected.DayOfWeek - (int)anchor.DayOfWeek) + 7) % 7),

            // 年は呼ぶ側が日を決めてから渡す
            CalendarView.Year => anchor,

            // 月は日にちを保つ。月末が短ければそこで止める
            _ => new DateOnly(anchor.Year, anchor.Month,
                Math.Min(selected.Day, DateTime.DaysInMonth(anchor.Year, anchor.Month))),
        };
    }

    /// <summary>送った先の年度で、同じ月日にあたる日。</summary>
    private DateOnly SameDayInFiscalYear()
    {
        var selected = SelectedDate;

        // 年度は4月始まり。1〜3月は翌の暦年にあたる
        var year = selected.Month >= 4 ? Year.FiscalYear : Year.FiscalYear + 1;

        return new DateOnly(year, selected.Month,
            Math.Min(selected.Day, DateTime.DaysInMonth(year, selected.Month)));
    }

    // ------------------------------------------------------------------
    // カレンダーとタスクリスト
    //
    // Google に繋がなくても、このアプリだけで分類を作れる
    // ------------------------------------------------------------------

    private void AddSource(bool isTaskList)
    {
        var used = isTaskList ? [] : SourceLists.Calendars.Select(c => (string?)c.SwatchColor);
        var editor = new CalendarEditorViewModel(isTaskList, used);

        if (!_editors.ShowCalendarEditor(editor)) return;

        if (isTaskList)
        {
            _workspace.CreateTaskList(editor.TrimmedName);
            StatusMessage = $"タスクリスト「{editor.TrimmedName}」を作りました";
        }
        else
        {
            _workspace.CreateCalendar(editor.TrimmedName, editor.Color);
            StatusMessage = $"カレンダー「{editor.TrimmedName}」を作りました";
        }
    }

    private void EditSource(SourceListItemViewModel? target)
    {
        if (target is null) return;

        var isTaskList = IsTaskList(target);
        // 「inaCalendar」は名前で見分けて日付の行に出している。変えられると黙って止まる
        var locked = !isTaskList && string.Equals(
            target.Name, CalendarWorkspace.WorkingDayCalendarName, StringComparison.Ordinal);

        var editor = new CalendarEditorViewModel(
            target.Id, target.Name, target.SwatchColor, isTaskList, locked);

        if (!_editors.ShowCalendarEditor(editor)) return;

        // 画面で止めていても、名前は元のものを使う。入口が増えても崩れないようにする
        var name = locked ? target.Name : editor.TrimmedName;

        var changed = isTaskList
            ? _workspace.UpdateTaskList(target.Id, name)
            : _workspace.UpdateCalendar(target.Id, name, editor.Color);

        StatusMessage = changed ? $"「{name}」に変更しました" : "見つかりませんでした";
    }

    /// <summary>
    /// 消せるのはこのアプリのものだけ。
    /// <para>
    /// Google のものを手元から消しても、次の同期で一覧から戻ってくる。そのうえ中の予定は
    /// 別のカレンダーへ移されたまま取り残される（相手が変わっていなければ取り込みが
    /// 素通りするため）。消えたように見えて消えていない、いちばん分かりにくい形になる。
    /// </para>
    /// <para>見せたくないだけなら、左パネルのチェックを外せばよい。</para>
    /// </summary>
    private static bool CanDeleteSource(SourceListItemViewModel? target) =>
        target is not null && !target.IsGoogle;

    private void DeleteSource(SourceListItemViewModel? target)
    {
        if (target is null) return;

        var isTaskList = IsTaskList(target);
        var count = isTaskList
            ? _workspace.Sources.TaskCountIn(target.Id)
            : _workspace.Sources.EventCountIn(target.Id);

        var kind = isTaskList ? "タスクリスト" : "カレンダー";
        var contents = isTaskList ? "タスク" : "予定";

        // 移った先を名前で言う。「別のカレンダーへ移ります」だけだと、
        // 消したあとどこを見ればよいのか分からない
        var destination = isTaskList
            ? _workspace.TaskMoveTargetFor(target.Id)?.DisplayName
            : _workspace.MoveTargetFor(target.Id)?.DisplayName;

        // 中身ごと消さない。分類を消したかっただけなのに中身まで消えるのは行き過ぎ
        var message = count > 0 && destination is not null
            ? $"{kind}「{target.Name}」を削除します。{Environment.NewLine}{Environment.NewLine}"
              + $"入っている{contents} {count} 件は「{destination}」へ移ります。削除はされません。"
            : $"{kind}「{target.Name}」を削除します。";

        if (!_editors.Confirm($"{kind}の削除", message)) return;

        var moved = isTaskList ? _workspace.DeleteTaskList(target.Id) : _workspace.DeleteCalendar(target.Id);

        StatusMessage = moved switch
        {
            null => $"最後の{kind}は削除できません",
            0 => $"{kind}「{target.Name}」を削除しました",
            var n => $"{kind}「{target.Name}」を削除し、{contents} {n} 件を「{destination}」へ移しました",
        };
    }

    /// <summary>
    /// 左パネルの並べ替え。<paramref name="moved"/> を <paramref name="target"/> の位置へ移す。
    /// <para>
    /// 並び順は時刻を持たない予定の並びにも効く（要件どおり、旧 inaCalendar と同じ）ので、
    /// 見た目だけの話ではない。
    /// </para>
    /// <para>
    /// カレンダーとタスクリストの間では動かさない。別の一覧なので、混ぜる意味がない。
    /// </para>
    /// </summary>
    /// <param name="above">true なら target の上、false なら下に入れる。</param>
    /// <returns>実際に動いたら true。</returns>
    public bool MoveSource(
        SourceListItemViewModel? moved, SourceListItemViewModel? target, bool above = true)
    {
        ShowDropHint(null, above: false);

        if (!CanMoveSource(moved, target)) return false;

        var isTaskList = IsTaskList(moved!);

        var items = isTaskList ? SourceLists.TaskLists : SourceLists.Calendars;

        var ids = items.Select(i => i.Id).ToList();
        if (!ids.Remove(moved!.Id)) return false;

        // 抜いたあとに数え直す。先に位置を控えると、上へ動かすときに1つずれる
        var to = ids.IndexOf(target!.Id);
        if (to < 0) return false;

        ids.Insert(above ? to : to + 1, moved.Id);

        if (isTaskList) _workspace.Sources.SetTaskListOrder(ids);
        else _workspace.Sources.SetCalendarOrder(ids);

        // 並び順は予定の並びにも効くので、画面をまるごと引き直す
        RefreshViews();

        StatusMessage = $"「{moved.Name}」の位置を変えました";
        return true;
    }

    /// <summary>
    /// 並べ替えの最中に、落ちる位置を示す。
    /// <para>掴んだまま動かすたびに呼ぶ。<paramref name="target"/> が null なら消す。</para>
    /// </summary>
    public void ShowDropHint(SourceListItemViewModel? target, bool above)
    {
        foreach (var item in SourceLists.Calendars.Concat(SourceLists.TaskLists))
        {
            item.DropHint = ReferenceEquals(item, target)
                ? above ? DropHint.Above : DropHint.Below
                : DropHint.None;
        }
    }

    /// <summary>並べ替えとして成り立つ組み合わせか。落とせる先かどうかの判断に使う。</summary>
    public bool CanMoveSource(SourceListItemViewModel? moved, SourceListItemViewModel? target) =>
        moved is not null && target is not null &&
        !ReferenceEquals(moved, target) &&
        IsTaskList(moved) == IsTaskList(target);

    /// <summary>タスクリスト側の項目か。一覧に含まれているかで見分ける。</summary>
    private bool IsTaskList(SourceListItemViewModel target) =>
        SourceLists.TaskLists.Any(t => ReferenceEquals(t, target));

    // ------------------------------------------------------------------
    // 取り込み
    // ------------------------------------------------------------------

    /// <summary>
    /// 実働日ファイルを取り込む。
    /// <para>
    /// この口が無いと、配布の Excel を読み込む手段が無く、実働日の表示も計算も
    /// 動かないままになる（要件書 4.1）。
    /// </para>
    /// </summary>
    /// <summary>
    /// いまの内容をファイルに書き出す。
    /// <para>予定・タスク・設定・実働日データが1つのファイルに入る。</para>
    /// </summary>
    /// <summary>
    /// 同じ内容の予定を1つにまとめる。
    /// <para>
    /// 消す前に件数を出して尋ねる。Google に繋いでいれば、次の同期で向こうからも消える。
    /// </para>
    /// </summary>
    private void RemoveDuplicates()
    {
        var extra = _workspace.FindDuplicateEvents();
        if (extra.Count == 0)
        {
            StatusMessage = "同じ内容の予定は見つかりませんでした";
            return;
        }

        var sample = string.Join("\n", extra
            .Take(5)
            .Select(e => $"・{e.Date:M/d} {e.Title}"));

        var more = extra.Count > 5 ? $"\n…ほか {extra.Count - 5} 件" : string.Empty;

        if (!_files.Confirm(
                $"同じ内容の予定が {extra.Count} 件あります",
                $"次のものを削除します。中身の多いほうを1件ずつ残します。\n\n{sample}{more}"
                + "\n\nCtrl＋Z でまとめて戻せます。"
                + "\nGoogle に繋いでいれば、次の同期で向こうからも消えます。"))
        {
            return;
        }

        var removed = _workspace.RemoveDuplicateEvents();
        StatusMessage = $"重複していた予定 {removed} 件を削除しました";
    }

    /// <summary>
    /// 配信元から実働日データを取りに行く。
    /// <para>
    /// <paramref name="quiet"/> のときは起動時の自動取得。取れなくても黙って見送る。
    /// 繋がらない場所に置かれていることもあり、そのたびに断りを出しても仕方がない。
    /// </para>
    /// </summary>
    private async Task FetchFeedAsync(bool quiet)
    {
        if (_settings is not { FeedUrl.Length: > 0 } settings) return;

        try
        {
            var result = await _feed.FetchAsync(settings.FeedUrl).ConfigureAwait(true);

            _workspace.ApplyWorkingDays(result);
            settings.FeedCheckedOn = _today;

            StatusMessage = $"配信元から実働日を取り込みました（稼働日 {result.WorkingDays.Count} 件）";
        }
        catch (Exception ex) when (quiet && ex is not OperationCanceledException)
        {
            // 自動の取得はここで止める。次に開いたときにまた試す
            settings.FeedCheckedOn = _today;
        }
    }

    /// <summary>いまの実働日データを配信用のファイルに書き出す。</summary>
    private void ExportFeed()
    {
        if (_workspace.WorkingDays.Days.Count == 0)
        {
            StatusMessage = "書き出せる実働日データがありません";
            return;
        }

        if (_files.PickSaveFile(
                "配信用ファイルの保存先",
                "実働日データ (*.json)|*.json|すべてのファイル (*.*)|*.*",
                "feed.json") is not { } path)
        {
            return;
        }

        try
        {
            // 直書きせず、一時ファイルへ書いてから置き換える（DpapiTokenStore.Save と同じ流儀）。
            // 途中で失敗しても、配信中の feed.json を壊さない
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, WorkdayFeed.Write(_workspace.WorkingDays, _today));
            File.Move(temporary, path, overwrite: true);

            StatusMessage = $"実働日データを書き出しました（{Path.GetFileName(path)}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"書き出せませんでした（{ex.Message}）";
        }
    }

    private void Backup()
    {
        if (SaveBackup is null)
        {
            StatusMessage = "この画面からは書き出せません";
            return;
        }

        var name = $"SlideinaCalendar-{DateTime.Now:yyyyMMdd-HHmm}.db";
        if (_files.PickSaveFile("バックアップの保存先", BackupFilter, name) is not { } path) return;

        try
        {
            SaveBackup(path);
            StatusMessage = $"バックアップを書き出しました（{Path.GetFileName(path)}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"バックアップを書き出せませんでした（{ex.Message}）";
        }
    }

    /// <summary>
    /// 書き出したファイルで置き換える。
    /// <para>
    /// <b>いまの内容はすべて置き換わる。</b>元に戻せないので必ず尋ねる。
    /// 置き換えたあとはアプリを立ち上げ直す。
    /// </para>
    /// </summary>
    private void Restore()
    {
        if (RestoreBackup is null)
        {
            StatusMessage = "この画面からは復元できません";
            return;
        }

        if (_files.PickOpenFile("復元するバックアップを選ぶ", BackupFilter) is not { } path) return;

        if (!_files.Confirm(
                "バックアップから復元します",
                "いまの予定・タスク・設定・実働日データは、すべてファイルの内容に置き換わります。"
                + "元に戻すことはできません。\n\n復元したあとアプリを立ち上げ直します。"))
        {
            return;
        }

        // DB の入れ替えが絡むので Run() と同じ理由で別スレッドへは逃がさない。
        // 待機カーソルだけ出す（項目5）
        IsBusy = true;
        StatusMessage = "復元しています…";

        try
        {
            RestoreBackup(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            StatusMessage = $"復元できませんでした（{ex.Message}）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private const string BackupFilter = "SlideinaCalendar のバックアップ (*.db)|*.db|すべてのファイル (*.*)|*.*";

    private void ImportWorkingDays()
    {
        if (_files.PickOpenFile("実働日ファイルを選ぶ", "Excel ブック (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*")
            is not { } path) return;

        Run(path, "実働日の取り込み", stream =>
        {
            var result = _workspace.ImportWorkingDays(stream);

            var lines = new List<string>
            {
                $"バージョン: {result.Version}",
                $"稼働日: {result.WorkingDays.Count} 件"
                    + $"（{result.WorkingDayRangeStart:yyyy/M/d} 〜 {result.WorkingDayRangeEnd:yyyy/M/d}）",
                $"マイルストーン: {result.Milestones.Count} 件",
            };

            if (result.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"警告 {result.Warnings.Count} 件");
                lines.AddRange(result.Warnings);
            }

            return (string.Join(Environment.NewLine, lines),
                    $"実働日を取り込みました（稼働日 {result.WorkingDays.Count} 件）");
        });
    }

    /// <summary>
    /// クライアント設定を取り込む。
    /// <para>
    /// クライアント ID とシークレットを手で写させない。長い文字列の写し間違いは
    /// 認可が通らない形でしか現れず、原因が分かりにくい。
    /// </para>
    /// </summary>
    private void ImportGoogleClient()
    {
        if (_googleClient is null) return;

        if (_files.PickOpenFile(
                "クライアント設定を選ぶ（client_secret_….json）",
                "Google のクライアント設定 (*.json)|*.json|すべてのファイル (*.*)|*.*")
            is not { } path) return;

        try
        {
            var options = _googleClient.Import(path);

            StatusMessage = "Google のクライアント設定を取り込みました";
            Raise(nameof(HasGoogleClient));

            // これを呼ばないと「Google に接続…」が押せないままになる
            Sync.RefreshAvailability();

            // 読み込めただけでは同期は始まらない。次に何を押すかまで書く。
            // ここで手が止まると、設定したのに使えない状態に見える
            _files.ShowReport(
                "Google の設定を取り込みました",
                $"""
                 次に、⚙ メニューの「Google に接続…」を押してください。
                 ブラウザが開いて許可を求められます。許可すると、そのまま
                 1回目の同期が走ります。

                 クライアント ID: {options.ClientId}
                 保存先: {_googleClient.Path}

                 なお、OAuth 同意画面の公開ステータスが「テスト」のままだと、
                 更新トークンが7日で失効します。毎週つなぎ直すことになるので、
                 ご自身専用でも「本番」に切り替えてください。
                 """);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            _files.ShowReport(
                "Google 連携の準備",
                $"読み込めませんでした。{Environment.NewLine}{Environment.NewLine}{e.Message}");
        }
    }

    /// <summary>旧データを取り込む。まとめて書き込むので、先に断りを入れる。</summary>
    private void ImportLegacyBackup()
    {
        if (!_files.Confirm(
                "旧データの取り込み",
                "旧 inaCalendar のバックアップを取り込みます。\n\n" +
                "同じ識別子の予定とタスクは上書きされます。この操作は元に戻せません。"))
        {
            return;
        }

        if (_files.PickOpenFile("バックアップを選ぶ", "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*")
            is not { } path) return;

        Run(path, "旧データの取り込み", stream =>
        {
            var result = _workspace.ImportLegacyBackup(stream);

            var summary = $"予定 {result.Events.Count} 件、タスク {result.Tasks.Count} 件"
                + $"（うち ToDo から変換 {result.ConvertedTaskCount} 件）";

            return ($"{result.SourceApp} v{result.SourceVersion}{Environment.NewLine}"
                    + $"{summary}{Environment.NewLine}{Environment.NewLine}{result.FormatLog()}",
                    $"旧データを取り込みました（{summary}）");
        });
    }

    /// <summary>
    /// ファイルを開いて取り込み、結果を見せる。
    /// <para>読めないファイルを選んでも落とさない。何が起きたかを出して続ける。</para>
    /// <para>
    /// 大きめのファイルだと結果が出るまで固まって見える（項目5）。ここで実際に行う
    /// 解析・反映（<c>CalendarWorkspace</c> 側）は <c>ObservableCollection</c> を
    /// UI スレッドの外から更新することになりかねず、別スレッドへ逃がすのは見送った。
    /// <see cref="IsBusy"/> を立てて画面側（<c>MainWindow</c>）に待機カーソルを
    /// 出させるだけに留める。
    /// </para>
    /// </summary>
    private void Run(string path, string title, Func<Stream, (string Report, string Status)> import)
    {
        IsBusy = true;
        StatusMessage = $"{title}を実行しています…";

        try
        {
            using var stream = File.OpenRead(path);
            var (report, status) = import(stream);

            RefreshViews();
            StatusMessage = status;
            _files.ShowReport(title, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or FormatException or InvalidDataException
                                  or InvalidOperationException or JsonException)
        {
            // 「実行しています…」を出しっぱなしにしない
            StatusMessage = $"{title}に失敗しました（{e.Message}）";
            _files.ShowReport(title, $"取り込めませんでした。{Environment.NewLine}{Environment.NewLine}{e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------
    // 予定とタスクの編集。どれも CalendarWorkspace を通すので Undo が効く
    // ------------------------------------------------------------------

    /// <summary>いまの時刻。新しい予定の既定の開始時刻を決めるのに使う。</summary>
    private TimeOnly NowTime => TimeOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>選択している日に予定を足す。</summary>
    // ------------------------------------------------------------------
    // ドラッグで動かす
    //
    // 掴んで落とすのと、編集画面で日付を打ち直すのとでは手数が違う。
    // Ctrl を押しながらなら複製。どちらも Undo を通る
    // ------------------------------------------------------------------

    /// <summary>
    /// 予定を別の日へ移す。時刻はそのまま。<paramref name="copy"/> なら複製する。
    /// <para>月ビューのように、何日の予定かだけを変えるときに使う。</para>
    /// </summary>
    /// <returns>動かしたら true。</returns>
    public bool MoveEventTo(string? id, DateOnly date, bool copy = false) =>
        MoveEvent(id, date, TimeChange.Keep, null, copy);

    /// <summary>予定を別の日時へ移す。長さは保つ。終日だったものは1時間ぶんになる。</summary>
    /// <returns>動かしたら true。</returns>
    public bool MoveEventToTime(string? id, DateOnly date, TimeOnly start, bool copy = false) =>
        MoveEvent(id, date, TimeChange.SetTo, start, copy);

    /// <summary>予定を終日に変えて別の日へ移す。</summary>
    /// <returns>動かしたら true。</returns>
    public bool MoveEventToAllDay(string? id, DateOnly date, bool copy = false) =>
        MoveEvent(id, date, TimeChange.Clear, null, copy);

    /// <summary>移動のときに時刻をどう扱うか。</summary>
    private enum TimeChange
    {
        /// <summary>そのまま。</summary>
        Keep,

        /// <summary>指定した時刻に置く。</summary>
        SetTo,

        /// <summary>時刻を外して終日にする。</summary>
        Clear,
    }

    /// <summary>
    /// 予定を動かす。
    /// <para>
    /// 期間のある予定は長さ（日数）を保つ。時刻を置くときは、その予定の長さ（時間）も保つ。
    /// </para>
    /// <para>
    /// 休業日と特別出勤は動かせない。識別子にその日付が入っていて、取り込んだ実働日
    /// データが決めるものだから。仕様期限などのラベルは動かせる。
    /// </para>
    /// </summary>
    /// <returns>動かしたら true。</returns>
    private bool MoveEvent(string? id, DateOnly date, TimeChange change, TimeOnly? start, bool copy)
    {
        if (id is not { Length: > 0 } || _workspace.Events.Find(id) is not { } found) return false;

        // 休業日と特別出勤はマスの色を決める印で、識別子にその日付が入っている。
        // 動かすと取り込んだ実働日データと食い違う。仕様期限などのラベルは動かせる
        if (CalendarWorkspace.IsClosedDayId(found.Id) || CalendarWorkspace.IsOpenDayId(found.Id))
        {
            StatusMessage = "休業日と特別出勤は実働日データが決めるので、動かせません";
            return false;
        }

        // 向こうで変えられない予定は動かさない。ここで動かしても伝わらず、
        // 画面と Google とで日付が食い違うだけ。複製は元を触らないので通す
        if (!copy && IsLocked(found))
        {
            StatusMessage = LockedMessage;
            return false;
        }

        var length = found.EndDate is { } end ? end.DayNumber - found.Date.DayNumber : 0;
        var moved = found with
        {
            Date = date,
            EndDate = found.EndDate is null ? null : date.AddDays(length),
        };

        moved = change switch
        {
            TimeChange.SetTo when start is { } at => moved with { StartTime = at, EndTime = EndOf(at, found) },
            TimeChange.Clear => moved with { StartTime = null, EndTime = null },
            _ => moved,
        };

        // 時刻だけを動かしたときは、日付が同じでも動かしたことになる
        if (moved.Date == found.Date && moved.StartTime == found.StartTime
            && moved.EndTime == found.EndTime && !copy)
        {
            return false;
        }

        if (!copy)
        {
            if (!_workspace.UpdateEvent(moved)) return false;

            StatusMessage = "予定を移しました";
            return true;
        }

        // 複製は向こうにまだ無いものとして作る。相手側の識別子を引き継ぐと、
        // 次の同期で元の予定のほうが書き換わる
        _workspace.AddEvent(moved with
        {
            Id = Guid.NewGuid().ToString("N")[..15],
            GoogleEventId = null,
            GoogleRaw = null,
            GoogleUpdated = null,
        });

        StatusMessage = "予定を複製しました";
        return true;
    }

    /// <summary>予定の長さ。時刻を持たない予定は1時間として扱う。</summary>
    private static TimeSpan LengthOf(CalendarEvent value) =>
        value.StartTime is { } from && value.EndTime is { } to && to > from
            ? to.ToTimeSpan() - from.ToTimeSpan()
            : TimeSpan.FromHours(1);

    /// <summary>
    /// 移した先での終わりの時刻。
    /// <para>
    /// <b>日をまたがせない。</b>またぐと終わりが始まりより前になり、時間軸に
    /// 置けなくなって画面から消える。遅い時刻に移したときは、その日の終わりで止める。
    /// </para>
    /// </summary>
    private static TimeOnly EndOf(TimeOnly start, CalendarEvent value)
    {
        var end = start.Add(LengthOf(value));

        return end > start ? end : new TimeOnly(23, 59);
    }

    /// <summary>タスクの期限を別の日へ移す。<paramref name="copy"/> なら複製する。</summary>
    /// <returns>動かしたら true。</returns>
    public bool MoveTaskTo(string? id, DateOnly date, bool copy = false)
    {
        if (id is not { Length: > 0 } || _workspace.Tasks.Find(id) is not { } found) return false;
        if (found.Due == date && !copy) return false;

        var moved = found with { Due = date };

        if (!copy)
        {
            if (!_workspace.UpdateTask(moved)) return false;

            StatusMessage = "タスクの期限を移しました";
            return true;
        }

        _workspace.AddTask(moved with
        {
            Id = Guid.NewGuid().ToString("N")[..15],
            GoogleTaskId = null,
            GoogleRaw = null,
        });

        StatusMessage = "タスクを複製しました";
        return true;
    }

    /// <summary>
    /// 向こうで内容を変えられない予定か。
    /// <para>
    /// メールから起こされた予約（美容室やホテルなど）、誕生日、勤務場所がこれにあたる。
    /// <b>読むだけにする。</b>こちらで変えても向こうへは伝わらず、画面と Google とで
    /// 食い違うだけになる。消すことはできるので、削除は止めない。
    /// </para>
    /// </summary>
    public static bool IsLocked(CalendarEvent value) => EventMapper.IsLocked(value);

    private const string LockedMessage =
        "この予定は Google 側で作られたもので、ここからは変えられません（削除はできます）";

    private void AddEvent()
    {
        var editor = new EventEditorViewModel(SelectedDate, CalendarNames, NowTime, QuickCalendarId);
        if (!_editors.ShowEventEditor(editor)) return;

        _workspace.AddEvent(editor.ToModel());
        StatusMessage = "予定を追加しました";
    }

    private void EditEvent(DayEventViewModel? target) => EditEventBy(target?.Id);

    /// <summary>
    /// 識別子だけで予定を開く。
    /// <para>
    /// 月ビューのマスに並ぶ予定は右ペインの行とは別の型なので、識別子で受ける。
    /// 画面ごとに同じ処理を書くと、片方だけ直し忘れる。
    /// </para>
    /// </summary>
    private void EditEventBy(string? id)
    {
        if (id is not { Length: > 0 }) return;

        // 表示用の複製ではなく保存されている内容を直す。繰り返しの展開を書き戻さないため
        if (_workspace.Events.Find(id) is not { } stored) return;

        if (IsLocked(stored))
        {
            StatusMessage = LockedMessage;
            return;
        }

        var editor = new EventEditorViewModel(stored, CalendarNames);
        if (!_editors.ShowEventEditor(editor))
        {
            // 編集画面の「削除」から閉じたときは、保存はされていないが削除は行う
            if (editor.Deleted) DeleteEventBy(id);
            return;
        }

        StatusMessage = _workspace.UpdateEvent(editor.ToModel())
            ? "予定を変更しました"
            : "予定が見つかりませんでした";
    }

    /// <summary>
    /// 予定を削除する。
    /// <para>
    /// 確認ダイアログは出さない。Undo（Ctrl＋Z、ステータス行の「元に戻す」）で
    /// 戻せるので、1クリックごとに尋ねるのは二重の手間になる。
    /// </para>
    /// </summary>
    private void DeleteEvent(DayEventViewModel? target)
    {
        if (target is null) return;

        StatusMessage = _workspace.DeleteEvent(target.Id)
            ? "予定を削除しました"
            : "予定が見つかりませんでした";
    }

    /// <inheritdoc cref="EditEventBy"/>
    private void DeleteEventBy(string? id)
    {
        if (id is not { Length: > 0 }) return;

        StatusMessage = _workspace.DeleteEvent(id)
            ? "予定を削除しました"
            : "予定が見つかりませんでした";
    }

    /// <summary>
    /// 時間軸の1件を開く。
    /// <para>
    /// 作業時間ブロックは予定ではないので、もとになったタスクのほうを開く。
    /// ブロック自身の識別子で予定を探しても見つからない。
    /// </para>
    /// </summary>
    private void EditBlock(TimeBlockViewModel? target)
    {
        if (target is null) return;

        if (target.IsWorkBlock) EditTaskBy(target.TaskId);
        else EditEventBy(target.Id);
    }

    /// <inheritdoc cref="EditEventBy"/>
    private void EditTaskBy(string? id)
    {
        if (id is not { Length: > 0 } || _workspace.Tasks.Find(id) is not { } stored) return;

        var editor = new TaskEditorViewModel(stored, TaskListNames, _today);
        if (!_editors.ShowTaskEditor(editor))
        {
            // 編集画面の「削除」から閉じたときは、保存はされていないが削除は行う
            if (editor.Deleted) DeleteTaskBy(id);
            return;
        }

        StatusMessage = _workspace.UpdateTask(editor.ToModel())
            ? "タスクを変更しました"
            : "タスクが見つかりませんでした";
    }

    /// <inheritdoc cref="EditEventBy"/>
    private void DeleteTaskBy(string? id)
    {
        if (id is not { Length: > 0 }) return;

        StatusMessage = _workspace.DeleteTask(id)
            ? "タスクを削除しました"
            : "タスクが見つかりませんでした";
    }

    /// <summary>選択している日を期限にしてタスクを足す。</summary>
    private void AddTask()
    {
        var editor = new TaskEditorViewModel(SelectedDate, TaskListNames, _today, SourceLists.DefaultTaskList?.Id);
        if (!_editors.ShowTaskEditor(editor)) return;

        _workspace.AddTask(editor.ToModel());
        StatusMessage = "タスクを追加しました";
    }

    private void EditTask(TaskListItemViewModel? target)
    {
        if (target is null) return;
        if (_workspace.Tasks.Find(target.Id) is not { } stored) return;

        var editor = new TaskEditorViewModel(stored, TaskListNames, _today);
        if (!_editors.ShowTaskEditor(editor))
        {
            // 編集画面の「削除」から閉じたときは、保存はされていないが削除は行う
            if (editor.Deleted) DeleteTask(target);
            return;
        }

        StatusMessage = _workspace.UpdateTask(editor.ToModel())
            ? "タスクを変更しました"
            : "タスクが見つかりませんでした";
    }

    private void DeleteTask(TaskListItemViewModel? target)
    {
        if (target is null) return;

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

    /// <summary>
    /// 月・週・日ビューのタスクチップの右クリックメニューから、完了を切り替える。
    /// <para><see cref="ToggleTaskDone"/> と同じ動きを、<see cref="TaskItem"/> を持つ側にも提供する。</para>
    /// </summary>
    private void ToggleTaskChipDone(TaskItem? target)
    {
        if (target is null || !_workspace.ToggleTaskDone(target.Id)) return;

        StatusMessage = target.IsDone ? "タスクの完了を取り消しました" : "タスクを完了にしました";
    }

    /// <summary>編集画面に出すカレンダーの候補。名前で選ばせ、保存するのは ID。</summary>
    private IReadOnlyList<SourceChoice> CalendarNames =>
        SourceLists.Calendars.Select(c => new SourceChoice(c.Id, c.Name)).ToArray();

    private IReadOnlyList<SourceChoice> TaskListNames =>
        SourceLists.TaskLists.Select(t => new SourceChoice(t.Id, t.Name)).ToArray();

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

        // 1分ごとに、いま知らせるものがあるかを見る
        _reminders?.Check(now);
    }

    /// <summary>
    /// データが変わったので表示を引き直す。
    /// <para>
    /// <b>カレンダー一覧を先に読み直す。</b>各ビューは予定の帯の色と、出すかどうかの
    /// 判断をこの一覧から引くので、
    /// 順番が逆だと、色を変えた直後の引き直しに古い色が使われる。実機で、カレンダーの色を
    /// 変えても月ビューの帯が変わらなかった。
    /// </para>
    /// </summary>
    private void RefreshViews()
    {
        SourceLists.Refresh();

        Month.Refresh();
        SelectedDay.Refresh();
        Week.Refresh();
        Day.Refresh();
        MiniCalendar.Refresh();

        // 年と一覧は重い。年は12か月ぶん、一覧は数年ぶんの予定を組み立てる。
        // 出していないあいだに組み直しても誰も見ないので、出すときまで待つ
        _yearStale = true;
        _agendaStale = true;
        RefreshHeavyIfShown();

        RaiseHeader();
    }

    /// <summary>
    /// 月・週・日・ミニ月暦を組み立てる。
    /// <para>
    /// 週の始まりと表示時間帯はそれぞれの ViewModel が作られるときに決まるので、
    /// 設定が変わったときは組み直す。
    /// </para>
    /// </summary>
    [MemberNotNull(nameof(Month), nameof(MiniCalendar), nameof(Week), nameof(Day),
        nameof(Year), nameof(Agenda), nameof(SlimMonth))]
    private void BuildViews(DateOnly month, DateOnly selected)
    {
        var start = _settings?.DayStart;
        var end = _settings?.DayEnd;

        Month = new MonthViewModel(_workspace, month, _today, _weekStart, sources: SourceLists)
        {
            SelectedDate = selected,
        };
        MiniCalendar = new MiniCalendarViewModel(_workspace, month, _today, _weekStart)
        {
            SelectedDate = selected,
        };
        var hourHeight = _settings?.HourHeight ?? 0;

        Week = new WeekViewModel(
            _workspace, selected, _today, _weekStart, SourceLists, start, end, hourHeight);
        Day = new DayViewModel(_workspace, selected, _today, SourceLists, start, end, hourHeight);

        Year = new YearViewModel(
            _workspace, _today, _settings?.YearLayout ?? YearLayout.Grid, SourceLists)
        {
            SelectedDate = selected,
        };
        Year.GoTo(selected);

        // 出し方は年ビューの中のボタンで切り替える。年ビューを見ているときにしか
        // 関係しない選び方なので、設定画面には出さない（要件書 5.1）
        if (_settings is { } settings) Year.LayoutChanged += (_, layout) => settings.YearLayout = layout;

        Agenda = new AgendaViewModel(_workspace, _today, SourceLists);
        Agenda.GoTo(selected);

        // スリムパネルは自前の月暦を持つ。中央が週や日を出していても、
        // こちらはひと月の並びを見せ続ける
        SlimMonth = new MonthViewModel(_workspace, month, _today, _weekStart, sources: SourceLists)
        {
            SelectedDate = selected,
            IsCompact = true,
        };
    }

    /// <summary>設定が変わったあとに組み直す。出している月と選んでいる日は引き継ぐ。</summary>
    private void RebuildViews()
    {
        BuildViews(Month.Month, SelectedDate);

        Raise(nameof(Month), nameof(MiniCalendar), nameof(Week), nameof(Day),
            nameof(Year), nameof(Agenda));
        RefreshViews();
    }

    /// <summary>
    /// 実働日計算パネルを開く。
    /// <para>
    /// 選んでいる日を両方の欄の初期値にする。たいてい「今見ている日から数えたい」ので、
    /// 開いてすぐ日付を入れ直さずに済む。
    /// </para>
    /// </summary>
    /// <summary>
    /// いま開いている実働日計算パネル。閉じていれば null。
    /// <para>
    /// モードレスで出すようになったので、開いているあいだはカレンダー上のクリックを
    /// ここへ流す（<see cref="FeedWorkdayCalculator"/>）。
    /// </para>
    /// </summary>
    private WorkdayCalculatorViewModel? _openCalculator;

    /// <summary>
    /// 実働日計算パネルが開いているか。
    /// <para>カレンダー側が、日付クリックの流し先をこれで判断する。</para>
    /// </summary>
    public bool IsWorkdayCalculatorOpen => _openCalculator is not null;

    private void ShowWorkdayCalculator()
    {
        // すでに開いていれば、開いたままの内容を使う（作り直すと入力中のものが消える）
        if (_openCalculator is null)
        {
            _openCalculator = new WorkdayCalculatorViewModel(_workspace.WorkingDayMath, _today)
            {
                RangeFrom = SelectedDate,
                RangeTo = SelectedDate,
                BaseDate = SelectedDate,
            };
            _openCalculator.Closed += OnCalculatorClosed;
        }

        _editors.ShowWorkdayCalculator(_openCalculator);
    }

    private void OnCalculatorClosed(object? sender, EventArgs e)
    {
        if (sender is WorkdayCalculatorViewModel calculator) calculator.Closed -= OnCalculatorClosed;

        _openCalculator = null;
        Raise(nameof(IsWorkdayCalculatorOpen));
    }

    /// <summary>
    /// カレンダー上でクリックした日を、開いている実働日計算パネルへ流す。
    /// <para>
    /// 開いていなければ何もしない。閉じているときの日付選択の動きは変えない
    /// （呼び出し側は、いつもどおりの日付選択と一緒にこれを呼んでよい）。
    /// </para>
    /// </summary>
    /// <param name="date">クリックした日。</param>
    /// <param name="isEnd">Shift を押していたら true。「まで」に入る。</param>
    public void FeedWorkdayCalculator(DateOnly date, bool isEnd)
    {
        if (_openCalculator is not { } calculator) return;

        calculator.SetRange(
            isEnd ? calculator.RangeFrom : date,
            isEnd ? date : calculator.RangeTo);
    }

    /// <summary>年ビューを組み直す必要があるか。出していないあいだは溜めておく。</summary>
    private bool _yearStale;

    /// <inheritdoc cref="_yearStale"/>
    private bool _agendaStale;

    /// <summary>
    /// いま出しているほうだけ組み直す。
    /// <para>
    /// 年と一覧は重い。予定を1件足すたびに両方を組み直していたので、全体の動きが
    /// もたついていた。
    /// </para>
    /// </summary>
    private void RefreshHeavyIfShown()
    {
        if (_yearStale && _currentView == CalendarView.Year)
        {
            _yearStale = false;
            Year.Refresh();
        }

        if (_agendaStale && _currentView == CalendarView.Agenda)
        {
            _agendaStale = false;
            Agenda.Refresh();
        }
    }

    /// <summary>
    /// 粗いほうから細かいほうへの並び。
    /// <para>
    /// 一覧（全期間）・年・月・週・日。左へ行くほど広く、右へ行くほど狭い。
    /// Ctrl＋ホイールはこの並びを1つずつ動く。
    /// </para>
    /// </summary>
    private static readonly CalendarView[] ZoomOrder =
    [
        CalendarView.Agenda,
        CalendarView.Year,
        CalendarView.Month,
        CalendarView.Week,
        CalendarView.Day,
    ];

    /// <summary>
    /// 並びを <paramref name="step"/> だけ動く。
    /// <para>端では止まる。回し続けて一覧と日を行き来されると、どこに居るか見失う。</para>
    /// </summary>
    private void Zoom(int step)
    {
        var at = Array.IndexOf(ZoomOrder, _currentView);
        if (at < 0) return;

        var next = Math.Clamp(at + step, 0, ZoomOrder.Length - 1);
        if (next == at) return;

        CurrentView = ZoomOrder[next];
    }

    /// <summary>
    /// その日を選んでから、そのビューへ移る。
    /// <para>
    /// すでにそのビューを出しているときは <see cref="CurrentView"/> が動かないので、
    /// 日付を合わせるほうは自分で呼ぶ。
    /// </para>
    /// </summary>
    private void ShowOn(DateOnly? date, CalendarView view)
    {
        if (date is not { } day) return;

        SelectedDate = day;
        CurrentView = view;
        FocusOn(day);
    }

    /// <summary>
    /// どのビューへ移ってもその日が出ているようにする。
    /// <para>
    /// 年から日へ飛んでも、日から年へ戻っても、見ているものが変わらない。
    /// 「今日」ボタンも、どのビューでも同じように効く。
    /// </para>
    /// </summary>
    private void FocusOn(DateOnly date)
    {
        Month.GoTo(date);
        MiniCalendar.GoTo(date);
        Week.GoTo(date);
        Week.SelectedDate = date;
        Day.Date = date;

        // 年度が変われば、ここで組み直される。溜めてある印を下ろしておかないと、
        // このあと同じ組み立てをもう一度やることになる
        var fiscal = Year.FiscalYear;
        Year.SelectedDate = date;
        if (Year.FiscalYear != fiscal) _yearStale = false;

        Agenda.SelectedDate = date;
        Agenda.GoTo(date);

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
