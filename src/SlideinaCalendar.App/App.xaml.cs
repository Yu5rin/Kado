using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.App.Editing;
using SlideinaCalendar.App.Google;
using SlideinaCalendar.App.Update;
using SlideinaCalendar.App.Views;
using SlideinaCalendar.App.Notifications;
using SlideinaCalendar.App.Settings;
using SlideinaCalendar.App.Themes;
using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Data;
using SlideinaCalendar.Data.Backup;
using SlideinaCalendar.Presentation;
using SlideinaCalendar.Presentation.Sync;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App;

/// <summary>
/// アプリケーションの入口。
/// <para>
/// データベースを開き、スキーマを最新へ進めてからウィンドウを出す。
/// </para>
/// </summary>
public partial class App : Application
{
    private SqliteConnection? _connection;
    private GoogleConnection? _google;
    private SingleInstance? _instance;
    private BackgroundSync? _background;
    private UpdateService? _updater;

    /// <summary>
    /// 新しい版を見に行く先。
    /// <para>
    /// リポジトリが公開されていれば、認証なしで読める。非公開のうちは何も返らないので、
    /// 更新の確認は黙って見送られる（アプリの動きには差し支えない）。
    /// </para>
    /// </summary>
    private const string UpdateApiUrl =
        "https://api.github.com/repos/Yu5rin/SlideinaCalendar/releases/latest";

    private AppSettings? _settings;
    private Shell.ShellController? _shellController;
    private Shell.TrayIcon? _tray;
    private Shell.GlobalHotKeys? _hotKeys;
    private DockPlacementStore? _dockStore;

    /// <summary>閉じるボタンで終わるのではなくトレイに入る（要件書 7.4）。</summary>
    private bool _reallyExiting;

    /// <summary>異常終了の記録先。データベースと同じ場所に置く。</summary>
    private static string CrashLogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 入れ替え直後は、前のプロセスがまだ終わりきっていない。待たずに判定すると
        // 「すでに起動しています」で即座に終わり、更新したのに起動しないように見える
        var afterUpdate = e.Args.Contains(UpdateService.AfterUpdateArgument, StringComparer.Ordinal);

        // 2本動くと同期が壊れる。同じデータベースを開き、同じカレンダーへ書き戻すため
        _instance = SingleInstance.TryAcquire(afterUpdate ? UpdateService.AfterUpdateWait : TimeSpan.Zero);
        if (_instance is null)
        {
            // すでに動いているほうを前に出して、こちらは静かに終わる
            SingleInstance.AskRunningInstanceToShow();
            Shutdown();
            return;
        }

        // 拾わないと OS の「動作を停止しました」だけが出て、理由が何も残らない。
        //
        // あわせて AppBar を外す。外さずに落ちると、ワークエリアが削られたまま残り、
        // 最大化したウィンドウが画面いっぱいにならなくなる。アプリを消しても
        // 直らないので、ここで必ず戻す（要件書 2.3）
        DispatcherUnhandledException += (_, args) =>
        {
            ReleaseShell();
            ReportFatal(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        // Dispatcher を通らないところ（バックグラウンドのスレッドなど）で落ちても外す
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ReleaseShell();

            if (args.ExceptionObject is Exception fatal) ReportFatal(fatal);
        };

        // 終了の合図（サインアウトやシャットダウン）でも外す
        SessionEnding += (_, _) => ReleaseShell();

        // あとから開く窓（編集画面・設定・ショートカットなど）にも当てる。
        // タイトルバーは OS が描くので、窓ごとに頼まないと白いまま残る
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) TitleBarTheme.Apply(window);
            }));

        // 配色を当てるのはウィンドウを作る前。あとから当てると一瞬ちらつく。
        // 設定を読むにはデータベースが要るので、ここでは Windows に合わせておく
        ThemeManager.Apply(ThemeChoice.Auto);

        try
        {
            _connection = CalendarDatabase.OpenDefault().ConnectAndMigrate();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // データベースを開けないと何もできない。黙って落ちるより理由を見せる
            MessageBox.Show(
                $"データを開けませんでした。\n\n{ex.Message}\n\n保存先: {CalendarDatabase.DefaultPath}",
                "SlideinaCalendar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var workspace = new CalendarWorkspace(_connection);
        var today = DateOnly.FromDateTime(DateTime.Today);

        // 設定を読み、選ばれている配色に切り替える。自動のままなら当て直しても変わらない
        var settings = _settings = new AppSettings(workspace.Settings);
        ThemeManager.Apply(settings.Theme);
        settings.Changed += (_, _) => ThemeManager.Apply(settings.Theme);

        try
        {
            // 編集画面はウィンドウを親にして出す。その参照は作ったあとでないと渡せない
            MainWindow? window = null;
            var editors = new DialogEditorPresenter(() => window);
            var files = new ShellFileDialogs(() => window);

            // データベースと同じ場所に置く。デスクトップアプリ型のシークレットは
            // 秘密として扱えないので暗号化しない。守るべきはトークンのほう
            var googleClient = new SlideinaCalendar.Google.OAuth.GoogleClientSecretsStore(
                Path.Combine(
                    Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "google-client.json"));

            // トークンは DPAPI で守る。守るべきはこちら。クライアント設定のほうは
            // デスクトップアプリ型である以上どのみち手元に置かれ、秘密として扱えない
            _google = new GoogleConnection(
                workspace,
                googleClient,
                new DpapiTokenStore(Path.Combine(
                    Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "google-tokens.dat")),
                OpenInBrowser);

            // 前回、ワークエリアを削ったまま落ちていたら元に戻す（要件書 2.3）。
            // ウィンドウを作る前に済ませる。削られたままの画面を基準に位置を決めない
            _dockStore = new DockPlacementStore(workspace.Settings);
            Shell.WorkAreaGuard.RecoverIfNeeded(_dockStore);

            var dock = _dockStore.Load();

            window = new MainWindow
            {
                // 閉じたときの置き場所と大きさを覚え、次はそこで出す
                Placements = new WindowPlacementStore(workspace.Settings),
                DataContext = new MainViewModel(
                    workspace, today, editors: editors, files: files,
                    googleClient: googleClient, google: _google,
                    settings: settings, startup: new StartupRegistration(),
                    notifier: new ToastNotifier(), shell: dock),
            };

            MainWindow = window;

            // 閉じるボタンではトレイに入るだけにする。終了はトレイのメニューから
            window.Closing += OnMainWindowClosing;

            window.Show();

            SetUpShell(window);

            // 2本目が起動されたら、こちらを前に出す
            _instance.ListenForActivation(() => Dispatcher.Invoke(BringToFront));

            // 前回の入れ替えで残ったものを片付ける
            _updater = new UpdateService(UpdateApiUrl);
            _updater.CleanupOldFiles();

            // 裏でも静かに同期する。押し忘れても、開いている間は追いついていく
            if (window.DataContext is MainViewModel main)
            {
                main.CheckForUpdate = () => CheckForUpdateAsync(showWhenLatest: true);

                _background = new BackgroundSync(
                    token => Dispatcher.InvokeAsync(
                        () => main.Sync.SyncQuietlyAsync(token)).Task.Unwrap());

                _background.Start();
            }

            // 起動したときに一度だけ確かめる。最新なら何も出さない
            _ = CheckForUpdateAsync(showWhenLatest: false);

            WatchForResume(window);
        }
        catch (Exception ex)
        {
            // 画面を組み立てる前に落ちると Dispatcher のハンドラまで届かない
            ReportFatal(ex);
            Shutdown(1);
        }
    }

    /// <summary>異常終了を記録して見せる。ログに残さないと再現待ちになる。</summary>
    private static void ReportFatal(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"{DateTimeOffset.Now:O}\n{ex}\n\n");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても、この下の表示だけは出す
        }

        MessageBox.Show(
            $"予期しないエラーで終了します。\n\n{ex.GetType().Name}: {ex.Message}\n\n記録先: {CrashLogPath}",
            "SlideinaCalendar", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// 新しい版があるか確かめ、あれば案内する。
    /// <para>
    /// <paramref name="showWhenLatest"/> が false なら、最新のときは何も出さない。
    /// 起動のたびに「最新です」と言われても邪魔なだけ。
    /// </para>
    /// </summary>
    private async Task CheckForUpdateAsync(bool showWhenLatest)
    {
        if (_updater is null) return;

        try
        {
            var info = await _updater.CheckAsync().ConfigureAwait(true);

            if (info is null)
            {
                if (showWhenLatest)
                {
                    MessageBox.Show(
                        MainWindow,
                        $"お使いの {UpdateService.CurrentVersion} が最新です。",
                        "SlideinaCalendar", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            new UpdateWindow(_updater, info, () => Shutdown()) { Owner = MainWindow }.ShowDialog();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 更新を確かめられなくても、アプリは使える
            if (showWhenLatest)
            {
                MessageBox.Show(
                    MainWindow, "更新を確かめられませんでした。ネットワークをご確認ください。",
                    "SlideinaCalendar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// 隠れている窓を前に出す。
    /// <para>畳まれていたら開く。2本目を起動したときに、押した甲斐があるようにする。</para>
    /// </summary>
    private void BringToFront()
    {
        if (MainWindow is not { } window) return;

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// 認可のページを既定のブラウザで開く。
    /// <para>
    /// アプリの中に埋め込まない。認可はパスワードを入れる場面なので、利用者が
    /// いつも使っているブラウザの画面で、URL を自分で確かめられるほうがよい。
    /// </para>
    /// </summary>
    /// <summary>
    /// バックアップで置き換えて、アプリを立ち上げ直す。
    /// <para>
    /// 置き換えは接続を閉じてから。開いたまま差し替えると、書き込み待ちの内容と
    /// 食い違って壊れる。読み直すには立ち上げ直すのが確実で、途中の状態も残らない。
    /// </para>
    /// </summary>
    private void RestoreAndRestart(string backupPath)
    {
        _background?.Dispose();
        _background = null;
        _connection?.Dispose();
        _connection = null;

        DatabaseBackup.RestoreFrom(backupPath, CalendarDatabase.DefaultPath);

        if (Environment.ProcessPath is { Length: > 0 } exe)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            });
        }

        Shutdown();
    }

    private static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true,
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // いちばん先に外す。データベースを閉じたあとでは印を消せない
        ReleaseShell();

        _tray?.Dispose();
        _hotKeys?.Dispose();
        _background?.Dispose();
        _google?.Dispose();
        _connection?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------
    // シェル統合（要件書 2章・7章）
    // ------------------------------------------------------------------

    /// <summary>トレイ・ホットキー・居かたの制御を組み立てる。</summary>
    private void SetUpShell(MainWindow window)
    {
        if (window.DataContext is not MainViewModel main || _dockStore is null) return;

        _shellController = new Shell.ShellController(window, main.Shell, _dockStore);

        // 削れなかったときは理由を出す。黙って諦めると、押しても何も起きないとしか
        // 見えない。同じ理由を何度も出さないよう、1回だけにする
        var dockComplaint = (string?)null;
        _shellController.DockFailed += (_, reason) =>
        {
            if (string.Equals(dockComplaint, reason, StringComparison.Ordinal)) return;

            dockComplaint = reason;

            MessageBox.Show(
                window,
                $"{reason}\n\n画面端には寄せましたが、他のウィンドウを最大化すると重なります。",
                "SlideinaCalendar", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _shellController.Restore();

        _tray = new Shell.TrayIcon("SlideinaCalendar", BuildTrayMenu(main));
        _tray.Activated += (_, _) => Dispatcher.Invoke(BringToFront);

        // 他のアプリを使っているあいだでも効かせる。取られていれば黙って諦める
        _hotKeys = Shell.GlobalHotKeys.Attach(window);
        if (_hotKeys is not null) _hotKeys.Pressed += (_, kind) => Dispatcher.Invoke(() => OnHotKey(main, kind));

        // 居かたが変わるたびに控える。終了時だけだと、落ちたときに戻せない
        main.Shell.ModeChanged += (_, _) => _shellController?.Save();
        main.Shell.EdgeChanged += (_, _) => _shellController?.Save();
        main.Shell.DockWidthChanged += (_, _) => _shellController?.Save();
    }

    /// <summary>
    /// スリープから戻ったら、すぐ追いつく（要件書 7.5）。
    /// <para>
    /// 眠っているあいだタイマーは止まっている。起きたあと次の1分を待つと、その間に
    /// 知らせるはずだった予定が遅れる。<see cref="ReminderService"/> は「知らせる時刻を
    /// 過ぎていて、まだ始まっていないもの」を出す作りなので、起こしてやれば取り戻せる。
    /// </para>
    /// <para>
    /// この知らせは UI のスレッドには来ないので、渡し直してから触る。
    /// </para>
    /// </summary>
    private void WatchForResume(MainWindow window)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Exit += (_, _) => Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args)
        {
            if (args.Mode != Microsoft.Win32.PowerModes.Resume) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (window.DataContext is MainViewModel resumed) resumed.UpdateNow(DateTime.Now);
            });
        }
    }

    /// <summary>トレイのメニュー（要件書 7.4）。</summary>
    private System.Windows.Controls.ContextMenu BuildTrayMenu(MainViewModel main)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        void Add(string header, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("表示", BringToFront);
        menu.Items.Add(new System.Windows.Controls.Separator());

        Add("ウィンドウ", () => main.Shell.Mode = ShellMode.Window);
        Add("画面端に寄せる", () => main.Shell.Mode = ShellMode.Overlay);
        Add("ピン留め（画面を分割）", () => main.Shell.Mode = ShellMode.Dock);
        Add("左右を入れ替える", () => main.Shell.ToggleEdgeCommand.Execute(null));
        menu.Items.Add(new System.Windows.Controls.Separator());

        Add("いますぐ同期", () => main.Sync.SyncNowCommand.Execute(null));
        Add("ショートカット", () => main.ShowShortcutsCommand.Execute(null));
        Add("設定", () => main.OpenSettingsCommand.Execute(null));
        menu.Items.Add(new System.Windows.Controls.Separator());

        // ここでだけ本当に終わる。閉じるボタンはトレイに入るだけ
        Add("終了", () => { _reallyExiting = true; Shutdown(); });

        return menu;
    }

    private void OnHotKey(MainViewModel main, Shell.HotKeyKind kind)
    {
        BringToFront();

        // クイック入力の欄へ飛ばす。呼び出してから手で探させない
        if (kind == Shell.HotKeyKind.QuickEntry) MainWindow?.Focus();
    }

    /// <summary>
    /// 閉じるボタンではトレイに入るだけにする（要件書 7.4）。
    /// <para>終了はトレイのメニューから。更新のための終了はここを通らない。</para>
    /// </summary>
    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExiting || _tray is null) return;

        // 設定で切っていれば、そのまま終わる
        if (_settings is { CloseToTray: false }) return;

        e.Cancel = true;
        MainWindow?.Hide();
    }

    /// <summary>
    /// ワークエリアを元に戻す。
    /// <para>
    /// <b>何度呼んでも安全。</b>落ち方がいくつもあるので、それぞれの口から呼べるように
    /// してある。ここを通らずに落ちた場合は、次の起動で <c>WorkAreaGuard</c> が戻す。
    /// </para>
    /// </summary>
    private void ReleaseShell()
    {
        try
        {
            _shellController?.Dispose();
            _shellController = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // ここで例外を出すと、元の落ちた理由が見えなくなる
        }
    }
}
