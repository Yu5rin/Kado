using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Kado.App.Editing;
using Kado.App.Google;
using Kado.App.Update;
using Kado.App.Views;
using Kado.App.Notifications;
using Kado.App.Settings;
using Kado.App.Themes;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;
using Kado.Data;
using Kado.Data.Backup;
using Kado.Presentation;
using Kado.Presentation.Sync;
using Kado.Presentation.Update;
using Kado.Presentation.ViewModels;

namespace Kado.App;

/// <summary>
/// アプリケーションの入口。
/// <para>
/// データベースを開き、スキーマを最新へ進めてからウィンドウを出す。
/// </para>
/// </summary>
public partial class App : Application
{
    private SqliteConnection? _connection;

    /// <summary>
    /// Google 同期専用の接続。
    /// <para>
    /// <see cref="GoogleSyncService"/> は <c>ConfigureAwait(false)</c> で書かれているので、
    /// 最初の通信のあとはスレッドプール上のスレッドでリポジトリを叩く。<see cref="_connection"/>
    /// を UI スレッドと同時に使うと、どちらかが張っているトランザクションともう片方の
    /// トランザクション無しのコマンドがかち合い、<see cref="InvalidOperationException"/> で
    /// 落ちることがある。SQLite は WAL なので接続を分ければ読み書きを並行できる
    /// （書き込みどうしがかち合ったときは <c>busy_timeout</c> で待たせる。
    /// <see cref="CalendarDatabase.Connect"/> 側で設定済み）。
    /// </para>
    /// </summary>
    private SqliteConnection? _syncConnection;

    private GoogleConnection? _google;
    private SingleInstance? _instance;
    private BackgroundSync? _background;
    private UpdateService? _updater;

    /// <summary>
    /// 新しい版を見に行く先。
    /// <para>
    /// リポジトリは公開しているので、認証なしで読める。読めなかったとき（通信できない、
    /// 公開済みのリリースが無いなど）は <see cref="UpdateCheckStatus.Failed"/> になり、
    /// 起動時の確認なら黙って見送る（アプリの動きには差し支えない。理由は shell.log に残る）。
    /// </para>
    /// <para>
    /// 実際に見に行くのは、ここから組み立てる Atom フィード（<c>github.com/.../releases.atom</c>）が先。
    /// この URL は API の回数上限（未認証は1時間60回、同じ出口の IP で共有）を受けるので、
    /// 新しい版があるときだけ使う（<see cref="UpdateChecker"/>）。
    /// </para>
    /// </summary>
    private const string UpdateApiUrl =
        "https://api.github.com/repos/Yu5rin/Kado/releases/latest";

    private AppSettings? _settings;
    private Shell.ShellController? _shellController;
    private Shell.TrayIcon? _tray;
    private Shell.GlobalHotKeys? _hotKeys;
    private DockPlacementStore? _dockStore;

    /// <summary>閉じるボタンで終わるのではなくトレイに入る（要件書 7.4）。</summary>
    private bool _reallyExiting;

    /// <summary>
    /// 立ち上げ直す・終わるために、接続を閉じ始めたか。
    /// <para>閉じたあと終わるまでのわずかな間に画面のタイマーが読み込みを試みて失敗しても、予期しないエラーとして見せない。</para>
    /// </summary>
    private volatile bool _leaving;

    /// <summary>終了の問い合わせ（<c>WM_QUERYENDSESSION</c>）を受けて、WPF が自分を終わらせにかかったか。</summary>
    private volatile bool _sessionEndQueried;

    private Shell.SessionEndWatcher? _sessionWatcher;

    /// <summary>直近に「保存できませんでした」を出した時刻。同じ失敗の連続で窓を出し続けないための印。</summary>
    private DateTimeOffset? _lastRecoverableNoticeAt;

    /// <summary>異常終了の記録先。データベースと同じ場所に置く。</summary>
    private static string CrashLogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 起動にかかった時間の記録（項目B-5）。1回の起動で shell.log に1行（startup-timing）と、
        // 100ms を超えた処理ごとに1行（startup-slow）を残す。起点は Process.StartTime
        // （OS が数える、Main より前）なので、ここではもう何 ms か経っている。
        // 書き先を先に決めておかないと、これより前の処理を測れない
        StartupTrace.Sink = Shell.ShellDiagnosticsLog.Write;
        StartupTrace.Mark("OnStartup");

        base.OnStartup(e);

        // 入れ替え直後・立ち上げ直しの直後は、前のプロセスがまだ終わりきっていない。待たずに判定すると
        // 「すでに起動しています」で即座に終わり、更新したのに（復元したのに）起動しないように見える
        var waitsForPrevious = StartupArguments.WaitsForPreviousProcess(e.Args);

        // 2本動くと同期が壊れる。同じデータベースを開き、同じカレンダーへ書き戻すため
        _instance = SingleInstance.TryAcquire(waitsForPrevious ? StartupArguments.PreviousProcessWait : TimeSpan.Zero);
        if (_instance is null)
        {
            // すでに動いているほうを前に出して、こちらは静かに終わる
            SingleInstance.AskRunningInstanceToShow();
            Shutdown();
            return;
        }

        // 2本目の起動の合図は、できるだけ早く受け付ける。データベースを開く・案内を出す・窓を作る
        // あいだに2本目を起動されても、前に出てこないままにしない（窓がまだ無ければ何もしない）
        _instance.ListenForActivation(() => Dispatcher.InvokeAsync(BringToFront));

        // 拾わないと OS の「動作を停止しました」だけが出て、理由が何も残らない。
        //
        // あわせて AppBar を外す。外さずに落ちると、ワークエリアが削られたまま残り、
        // 最大化したウィンドウが画面いっぱいにならなくなる。アプリを消しても
        // 直らないので、ここで必ず戻す（要件書 2.3）
        DispatcherUnhandledException += (_, args) =>
        {
            // 復元の後片付けで接続を閉じたあと、終わるまでのわずかな間に来たもの。
            // これを「予期しないエラー」として見せると、復元できたのに失敗したように見える
            if (_leaving)
            {
                args.Handled = true;
                return;
            }

            // 画面のスレッドで漏れた、利用者の側で直せる失敗（ディスクがいっぱい・
            // ほかのアプリが使っている）。アプリごと終わらせず、案内して続ける
            if (UnhandledFailurePolicy.IsRecoverable(args.Exception))
            {
                args.Handled = true;
                Shell.ShellDiagnosticsLog.Write($"書き込みの失敗を案内して続行: {args.Exception.GetType().Name}: {args.Exception.Message}");

                // 画面のタイマーが同じ失敗を毎回漏らすことがある。続けて窓を出さない
                var now = DateTimeOffset.Now;
                if (UnhandledFailurePolicy.ShouldNotify(_lastRecoverableNoticeAt, now))
                {
                    _lastRecoverableNoticeAt = now;
                    Shell.FrontMessageBox.Show(
                        UnhandledFailurePolicy.Describe(args.Exception), Shell.FrontButtons.Ok, Shell.FrontIcon.Warning);
                }

                return;
            }

            ReleaseShell();
            ReportFatal(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        // Dispatcher を通らないところ（バックグラウンドのスレッドなど）で落ちても外す。
        // ここは画面以外のスレッドで来る。ReleaseShell は画面のスレッドのものを触るので、
        // 画面のスレッドへ渡し、終わるのを（時間を区切って）待つ
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ReleaseShellFromAnyThread();

            if (args.ExceptionObject is Exception fatal) ReportFatal(fatal);
        };

        // 終了の合図（サインアウトやシャットダウン）。
        // 保存と後片付けの準備だけをする。AppBar を外すのは、実際に終わるとき（OnExit）にする
        SessionEnding += (_, args) => HandleSessionEnding(args);

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

        // データベースを開く前に、DB を介さない印だけで前回の異常終了を確かめて戻す。
        // 壊れて開けなくなっていた場合、DB 版の印（下の RecoverIfNeeded(_dockStore)）は
        // 読めないので、ここが最後の砦になる（要件書 2.3）
        StartupTrace.Measure("WorkAreaGuard.RecoverIfNeeded", Shell.WorkAreaGuard.RecoverIfNeeded);

        // 見張りは、窓や接続より先に始める。ここから先で落ちても、取り消された終了の合図は受けられる
        _sessionWatcher = new Shell.SessionEndWatcher(OnSessionResolved);
        _sessionWatcher.Start();

        _connection = StartupTrace.Measure("DB接続・移行", OpenDatabaseOrAskUser);
        if (_connection is null) return;

        StartupTrace.Mark("DB接続後");

        // 起動時の自動バックアップ（世代保存）。取り込み系は Undo に積まないので、
        // 戻したいときの拠り所がこれしか無い。裏の別接続で取るので起動は待たせず、
        // 失敗しても起動そのものは止めない（AutoBackupService.TryRun が例外を外へ出さない）
        _ = AutoBackupService.RunInBackgroundAsync();

        var workspace = StartupTrace.Measure("CalendarWorkspace(画面用)", () => new CalendarWorkspace(_connection));
        var today = DateOnly.FromDateTime(DateTime.Today);

        // 設定を読み、選ばれている配色に切り替える。自動のままなら当て直しても変わらない
        // （起動時の1回目は ThemeChoice.Auto。ここで同じ Auto のままなら、
        // Theme.xaml を作り直す2回目は要らない）
        var settings = _settings = StartupTrace.Measure("AppSettings", () => new AppSettings(workspace.Settings));
        if (settings.Theme != ThemeManager.Current)
        {
            StartupTrace.Measure("ThemeManager.Apply", () => ThemeManager.Apply(settings.Theme));
        }

        // 設定が変わるたびに来るが、配色（Theme）が実際に変わったときだけ当て直す。
        // ThemeManager.Apply は Theme.xaml ごと作り直すので、無関係な設定
        // （通知音など）まで来るたびに払うには重い
        var appliedTheme = settings.Theme;
        settings.Changed += (_, _) =>
        {
            if (settings.Theme == appliedTheme) return;

            appliedTheme = settings.Theme;
            ThemeManager.Apply(appliedTheme);
        };

        try
        {
            // 編集画面はウィンドウを親にして出す。その参照は作ったあとでないと渡せない
            MainWindow? window = null;
            var editors = new DialogEditorPresenter(() => window);
            var files = new ShellFileDialogs(() => window);

            // データベースと同じ場所に置く。デスクトップアプリ型のシークレットは
            // 秘密として扱えないので暗号化しない。守るべきはトークンのほう
            var googleClient = new Kado.Google.OAuth.GoogleClientSecretsStore(
                Path.Combine(
                    Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "google-client.json"));

            // トークンは DPAPI で守る。守るべきはこちら。クライアント設定のほうは
            // デスクトップアプリ型である以上どのみち手元に置かれ、秘密として扱えない
            //
            // DPAPI は会社の PC（ドメイン参加）だと最初の呼び出しが遅いことがある。以下のデータベースを開く・組み立てる処理と重ねられるよう、
            // 先に別スレッドで読み始める（DpapiTokenStore は読み込みを1本にして、復号できた
            // 控えを覚える。あとで画面のスレッドが呼んでも二重には復号しない）
            var tokenStore = new DpapiTokenStore(
                Path.Combine(Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "google-tokens.dat"),
                Shell.ShellDiagnosticsLog.Write);
            _ = Task.Run(() => tokenStore.Load());

            // Google 同期には UI と別の接続・別の CalendarWorkspace を渡す（_syncConnection
            // のコメント参照）。同じファイルを見ているので、書き込んだ内容は同期が
            // 終わった時点で UI 側の接続からも読める。EnsureSources・実働日の組み直しは
            // Sync.Synced を受けた側（MainViewModel）が UI 側の workspace で読み直している
            _syncConnection = StartupTrace.Measure(
                "DB接続・移行(同期用)", () => CalendarDatabase.OpenDefault().ConnectAndMigrate());
            // 同期は実働日・マイルストーンを見ない（GoogleSyncService が触るのは
            // Sources／Events／Tasks／Tombstones／Settings と WorkingDayCalendars だけ）。
            // 全予定を読む LoadWorkingDays を起動のたびに2回払わなくてよいよう、
            // こちらは軽い構築にする（CalendarWorkspace のコンストラクタのコメント参照）
            var syncWorkspace = StartupTrace.Measure(
                "CalendarWorkspace(同期用)", () => new CalendarWorkspace(_syncConnection, loadWorkingDays: false));

            // ここで一度読んでおく。GoogleConnection が読むタイミングでは
            // 「復号に失敗したか」を伝える先が無いため、先に確かめておく
            // （DpapiTokenStore.DecryptionFailed を見るのは、ここと下の起動時通知の2か所だけ）
            //
            // DPAPI は、会社の PC（ドメイン参加）だと最初の呼び出しが遅いことがある。
            // 復号できた控えは DpapiTokenStore が覚えているので、2回目以降は呼ばない
            StartupTrace.Measure("DPAPI(トークン読み込み)", () => tokenStore.Load());

            _google = new GoogleConnection(
                syncWorkspace,
                googleClient,
                tokenStore,
                OpenInBrowser);

            // 前回、ワークエリアを削ったまま落ちていたら元に戻す（要件書 2.3）。
            // ウィンドウを作る前に済ませる。削られたままの画面を基準に位置を決めない
            _dockStore = new DockPlacementStore(workspace.Settings);
            Shell.WorkAreaGuard.RecoverIfNeeded(_dockStore);

            var dock = _dockStore.Load();
            StartupTrace.Mark("シェル設定の読み込み後");

            // 組み立ての順序は、もとの初期化子（MainWindow → Placements → Settings →
            // DataContext）と同じ。区間ごとに測るため、3つに分けてある
            var mainWindow = StartupTrace.Measure("MainWindow構築", () => new MainWindow
            {
                // 閉じたときの置き場所と大きさを覚え、次はそこで出す
                Placements = new WindowPlacementStore(workspace.Settings),

                // いちばん細くできる幅は設定から。窓の下限をそのまま決める
                Settings = settings,
            });
            window = mainWindow;
            StartupTrace.Mark("MainWindow構築後");

            var mainViewModel = StartupTrace.Measure("MainViewModel構築", () => new MainViewModel(
                workspace, today, editors: editors, files: files,
                googleClient: googleClient, google: _google,
                settings: settings, startup: new StartupRegistration(),
                notifier: new ToastNotifier(), shell: dock,
                // 添付は UI 側から Google ドライブへ直接アップロードする。
                // フォルダ ID の控えは UI 側の workspace.Settings に持つ（同じ
                // ファイルを同期用の接続とも共有している）
                attachmentUploader: new Kado.Presentation.Sync.GoogleDriveAttachmentUploader(
                    _google, workspace.Settings)));
            StartupTrace.Mark("MainViewModel構築後");

            // ここでバインディングが評価される。年・一覧のビューは表示するまで
            // 作らない（MainViewModel.YearForView／AgendaForView）
            StartupTrace.Measure("DataContext設定(バインディング評価)", () => mainWindow.DataContext = mainViewModel);

            // 「MainWindow を作り終えた時点」
            StartupTrace.Mark("画面作成");

            MainWindow = window;

            // 閉じるボタンではトレイに入るだけにする。終了はトレイのメニューから
            window.Closing += OnMainWindowClosing;

            // 起動にかかった時間の区間ごとの印。Show の中で何が起きているかを切り分ける
            // （窓のハンドルができた時点・最初のレイアウトが済んだ時点・Loaded）。
            // 1回の起動で複数回来ないよう、記録したらすぐ外す
            void OnSourceInitializedOnce(object? sender, EventArgs args)
            {
                window.SourceInitialized -= OnSourceInitializedOnce;
                StartupTrace.Mark("SourceInitialized");
            }

            void OnFirstLayoutUpdated(object? sender, EventArgs args)
            {
                window.LayoutUpdated -= OnFirstLayoutUpdated;
                StartupTrace.Mark("最初のLayoutUpdated");
            }

            void OnLoadedOnce(object? sender, RoutedEventArgs args)
            {
                window.Loaded -= OnLoadedOnce;
                StartupTrace.Mark("Loaded");
            }

            window.SourceInitialized += OnSourceInitializedOnce;
            window.LayoutUpdated += OnFirstLayoutUpdated;
            window.Loaded += OnLoadedOnce;

            // 起動にかかった時間の最後の1点。最初の描画が終わったところで
            // 印を1行にまとめて書く（項目B-5）。1回の起動で複数回来ないよう、
            // 書いたらすぐ外す。これ以後、起動の記録は止まる
            void LogStartupTiming(object? sender, EventArgs args)
            {
                window.ContentRendered -= LogStartupTiming;

                StartupTrace.Finish("ContentRendered");
            }

            window.ContentRendered += LogStartupTiming;

            // スライド・ピン留めで始まるときは、最初からタスクバーに出さない。
            // 出してからでは、Restore までの一瞬だけボタンが見えてしまう。
            // 窓のハンドルができる前（Show より前）なら、作り直しも起きない
            window.ShowInTaskbar = Shell.ShellGeometry.ShowsInTaskbar(dock.Mode);

            StartupTrace.Mark("Show開始");
            window.Show();
            StartupTrace.Mark("Show終了");

            StartupTrace.Measure("SetUpShell", () => SetUpShell(window));

            // 保存されていたトークンが、退避して読み直してもなお復号できなかったときだけ、理由を一度伝える
            // （DpapiTokenStore.Load。1度目の失敗は一時的かもしれないので伝えない）。
            // 黙って「Google 未接続」に戻ると、Windows パスワードの強制リセットや
            // プロファイル移行のあとに理由が分からなくなる。
            //
            // 案内は最初の画面が出て、トレイや2本目の起動の受け付けが整ったあとに回す。
            // 先に出すと、その間に2本目を起動しても前に出てこない
            if (tokenStore.DecryptionFailed)
            {
                Dispatcher.InvokeAsync(
                    () => Shell.FrontMessageBox.Show(
                        "保存されていた Google の接続情報を、何度か試しても読めませんでした。"
                        + "Google に接続し直してください。\n\n"
                        + "Windows のパスワードを変えた、または別の PC やプロファイルへ移したときに起こります。",
                        Shell.FrontButtons.Ok, Shell.FrontIcon.Warning),
                    DispatcherPriority.ApplicationIdle);
            }

            // 前回の入れ替えで残ったものを片付ける。ファイルの削除（同期I/O）なので、
            // 最初の画面が出てからでよい。ApplicationIdle まで待たせば、初回描画の
            // あとに回る。CleanupOldFiles は他の起動処理を待たない独立した後片付けで、
            // 何かの前提になっていない（_updater フィールド自体はここで先に作っておく）
            // 更新の確認・ダウンロードの各段階は shell.log に1行ずつ残す（会社のネットワークで
            // だけ更新できない、という報告を、理由まで追えるようにするため）
            _updater = StartupTrace.Measure(
                "UpdateService構築", () => new UpdateService(UpdateApiUrl, Shell.ShellDiagnosticsLog.Write));
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _updater.CleanupOldFiles());

            // 裏でも静かに同期する。押し忘れても、開いている間は追いついていく
            if (window.DataContext is MainViewModel main)
            {
                main.CheckForUpdate = () => CheckForUpdateAsync(showWhenLatest: true);
                main.CheckUpdateConnection = () => _updater!.ProbeConnectionAsync();

                // バックアップの書き出し・復元の口を結ぶ。ここを結ばないと、⚙メニューの
                // 「バックアップ」は常に「この画面からは書き出せません」を返し、
                // 「復元」は CanExecute が false のまま押せない
                main.SaveBackup = backupPath => DatabaseBackup.SaveTo(_connection!, backupPath);
                main.RestoreBackup = RestoreAndRestart;

                // 取り込みの直前にも世代バックアップを取る。取り込みは Undo に
                // 積まないので、これが唯一の戻り道になる
                main.AutoBackupBeforeImport = () =>
                {
                    if (_connection is { } current) AutoBackupService.TryRun(current);
                };

                // RelayCommand は CommandManager に乗っていない。RestoreBackup を
                // あとから入れても、これを呼ばないと「復元」ボタンが無効のまま戻らない
                main.RestoreCommand.RaiseCanExecuteChanged();

                _background = new BackgroundSync(
                    token => Dispatcher.InvokeAsync(
                        () => main.Sync.SyncQuietlyAsync(token)).Task.Unwrap());

                _background.Start();
            }

            StartupTrace.Mark("同期の準備後");

            // 起動したときに一度だけ確かめる。最新なら何も出さない。
            // 設定で切れる（⚙メニューからの手動確認はこの設定に関わらず動く。
            // main.CheckForUpdate 経由で呼ぶほうなので、ここでは分岐しない）
            if (settings.CheckForUpdateOnStartup)
            {
                // 通信そのものは別スレッドで走る（UpdateService.CheckAsync）。
                // ここで測るのは、呼び出しが画面のスレッドを止めた時間
                StartupTrace.Measure("更新の確認(開始)", () => { _ = CheckForUpdateAsync(showWhenLatest: false); });
            }

            StartupTrace.Measure("WatchForResume", () => WatchForResume(window));
            StartupTrace.Mark("OnStartup終了");
        }
        catch (Exception ex)
        {
            // 画面を組み立てる前に落ちると Dispatcher のハンドラまで届かない
            ReportFatal(ex);
            Shutdown(1);
        }
    }

    /// <summary>これを超えたら世代を1つずらす。無制限に育てない。</summary>
    private const long CrashLogMaxBytes = 1_000_000;

    /// <summary>異常終了を記録して見せる。ログに残さないと再現待ちになる。</summary>
    private static void ReportFatal(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            RotateCrashLogIfTooBig();
            File.AppendAllText(CrashLogPath, $"{DateTimeOffset.Now:O}\n{ex}\n\n");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても、この下の表示だけは出す
        }

        // 親の窓が無い（起動の途中・別のスレッド）ことも多い。裏に隠れて気づかれないまま
        // 終わらないよう、前に出す
        Shell.FrontMessageBox.Show(
            $"予期しないエラーで終了します。\n\n{ex.GetType().Name}: {ex.Message}\n\n記録先: {CrashLogPath}",
            Shell.FrontButtons.Ok, Shell.FrontIcon.Error);
    }

    /// <summary>
    /// crash.log が無制限に育たないようにする。
    /// <para>
    /// 上限を超えていたら <c>crash.log.1</c> へ退避して、新しく書き始める。世代は
    /// 1つだけ持てば十分――何世代も残しても、古いものまで読みに戻ることは無い。
    /// </para>
    /// </summary>
    private static void RotateCrashLogIfTooBig()
    {
        if (!File.Exists(CrashLogPath)) return;
        if (new FileInfo(CrashLogPath).Length < CrashLogMaxBytes) return;

        var previous = CrashLogPath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(CrashLogPath, previous);
    }

    /// <summary>
    /// 新しい版があるか確かめ、あれば案内する。
    /// <para>
    /// <paramref name="showWhenLatest"/> が false なら、最新のとき・確認中で始められ
    /// なかったときは何も出さない。起動のたびに「最新です」と言われても邪魔なだけ。
    /// </para>
    /// <para>
    /// 起動直後の裏の確認と、押しての確認が重なることがある。以前は2本目が
    /// <c>null</c> を受け取って「最新です」と誤って言っていたので、4状態
    /// （<see cref="UpdateCheckStatus"/>）を区別して扱う。
    /// </para>
    /// </summary>
    private async Task CheckForUpdateAsync(bool showWhenLatest)
    {
        if (_updater is null) return;

        // 押して確かめたときは、待っていることが分かるようにする
        if (showWhenLatest) Mouse.OverrideCursor = Cursors.Wait;

        UpdateCheckResult result;
        try
        {
            result = await _updater.CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            // 待機カーソルは、通信を待つあいだだけ。結果の窓やメッセージを出す前に戻す。
            // 更新の窓（ShowDialog）が閉じるまで残ると、窓の上で砂時計のままになる
            if (showWhenLatest) Mouse.OverrideCursor = null;
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.AlreadyChecking:
                if (showWhenLatest)
                {
                    MessageBox.Show(
                        MainWindow,
                        "いま確認しています。少し待ってからもう一度お試しください。",
                        "Kado", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;

            case UpdateCheckStatus.Failed:
                // 起動時の確認は黙って見送る（理由は shell.log に残っている）
                if (showWhenLatest) ShowUpdateFailure(result.Failure);

                return;

            case UpdateCheckStatus.UpToDate:
                if (showWhenLatest)
                {
                    MessageBox.Show(
                        MainWindow,
                        $"お使いの {UpdateService.CurrentVersion} が最新です。",
                        "Kado", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;

            case UpdateCheckStatus.UpdateAvailable:
                new UpdateWindow(_updater, result.Info!, () => Shutdown()) { Owner = MainWindow }.ShowDialog();
                return;
        }
    }

    /// <summary>
    /// 更新を確かめられなかったことを、理由に合わせた文言で伝える。
    /// <para>
    /// 「ネットワークをご確認ください」だけでは、会社の回線の上限（403）やプロキシの認証（407）の
    /// ときに次の一手が分からない。失敗したときも、リリースのページへ行ける道を残す
    /// （自動更新が通らない人ほど、手で入れ替えるしかない）。
    /// </para>
    /// </summary>
    private void ShowUpdateFailure(UpdateFailure? failure)
    {
        var message = failure?.Message ?? "更新を確かめられませんでした。ネットワークをご確認ください。";
        var page = failure?.ReleasePageUrl ?? string.Empty;

        // 応答から来た値ではなく自分で組み立てた URL だが、開く前に行き先を確かめる流儀は揃える
        var canOpen = page.Length > 0 && ReleaseFeed.IsAllowedDownloadUrl(page);

        if (!canOpen)
        {
            MessageBox.Show(MainWindow, message, "Kado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var answer = MessageBox.Show(
            MainWindow, message + "\n\nリリースのページを開きますか？",
            "Kado", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 既定のブラウザが無いなど。開けなくてもアプリの動きには差し支えない
        }
    }

    /// <summary>
    /// 隠れている窓を前に出す。
    /// <para>畳まれていたら開く。2本目を起動したときに、押した甲斐があるようにする。</para>
    /// </summary>
    private void BringToFront()
    {
        if (MainWindow is not { } window) return;

        // 居かたに応じた出し方（スライドなら最前面へ入れ直す）は ShellController が持つ。
        // スライド・ピン留めはタスクバーにボタンが無いので、ここで前に出せないと
        // 押したのに何も起きないように見える
        if (_shellController is { } controller)
        {
            controller.Show();
            return;
        }

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// バックアップで置き換えて、アプリを立ち上げ直す。
    /// <para>
    /// 置き換えは接続を閉じてから。開いたまま差し替えると、書き込み待ちの内容と
    /// 食い違って壊れる。読み直すには立ち上げ直すのが確実で、途中の状態も残らない。
    /// </para>
    /// <para>
    /// <b>半分止まった状態で動き続けない。</b>
    /// 選ばれたファイルが読めるかは、何かを閉じる前に確かめる（読めなければ例外のまま返り、
    /// 画面は何も閉じていないので、そのまま使える）。閉じたあとに置き換えが失敗したときは、
    /// 元のデータのまま立ち上げ直す（<see cref="DatabaseBackup.RestoreFrom"/> は、本体へ触れる前に
    /// 別名へ写してから置き換えるので、失敗しても元のデータは残っている）。
    /// </para>
    /// </summary>
    private void RestoreAndRestart(string backupPath)
    {
        // 何も壊す前に確かめる。違うファイルを選んでも、ここで例外になって画面に戻れる
        DatabaseBackup.EnsureReadableDatabase(backupPath);

        // 復元の直前にも世代バックアップを1本残す（項目4）。復元は「いまの内容を
        // すべて置き換える」破壊的操作で、選んだファイルを取り違えても後戻りできない。
        // 接続を閉じる前、まだ読める間に取る。失敗しても復元そのものは止めない
        if (_connection is { } current) AutoBackupService.TryRun(current);

        // ここから先は、画面が使う接続を閉じる。終わるまでのわずかな間に画面のタイマーが
        // 読み込みを試みても、予期しないエラーとして見せない
        _leaving = true;

        _background?.Dispose();
        _background = null;
        _google?.Dispose();
        _google = null;
        _connection?.Dispose();
        _connection = null;
        _syncConnection?.Dispose();
        _syncConnection = null;

        try
        {
            DatabaseBackup.RestoreFrom(backupPath, CalendarDatabase.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or FileNotFoundException or SqliteException)
        {
            // 接続を閉じたあとなので、このまま動き続けられない。元のデータのまま立ち上げ直す
            Shell.ShellDiagnosticsLog.Write($"復元: 失敗。元のデータのまま立ち上げ直します。{ex.GetType().Name}: {ex.Message}");

            Shell.FrontMessageBox.Show(
                "バックアップから復元できませんでした。元のデータのまま、Kado を立ち上げ直します。\n\n"
                + (ex is SqliteException sqlite ? SqliteFailure.DescribeReason(sqlite) : ex.Message),
                Shell.FrontButtons.Ok, Shell.FrontIcon.Warning);
        }

        RestartProcess();
    }

    /// <summary>
    /// 認可のページを既定のブラウザで開く。
    /// <para>
    /// アプリの中に埋め込まない。認可はパスワードを入れる場面なので、利用者が
    /// いつも使っているブラウザの画面で、URL を自分で確かめられるほうがよい。
    /// ブラウザを起動できないと <see cref="System.ComponentModel.Win32Exception"/> になる
    /// （呼び出し側が文言にする）。
    /// </para>
    /// </summary>
    private static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// いまの exe をもう一度起動して、自分は終わる。
    /// <para>
    /// <b>前のプロセスの終了を待つ引数を付ける。</b>付けないと、新しいほうの二重起動の判定が、
    /// まだミューテックスを持っている自分と競合し、「すでに起動しています」で即座に終わる。
    /// 起動できなかったときは理由を出す（黙って終わると、アプリが消えたように見える）。
    /// </para>
    /// </summary>
    private void RestartProcess()
    {
        _leaving = true;

        if (!TryLaunchSelf())
        {
            Shell.FrontMessageBox.Show(
                "Kado を自動で立ち上げ直せませんでした（セキュリティ ソフトなどに止められた可能性があります）。"
                + "いったん終了します。もう一度、Kado を起動してください。",
                Shell.FrontButtons.Ok, Shell.FrontIcon.Warning);
        }

        Shutdown();
    }

    /// <summary>自分の exe を、前のプロセスの終了を待つ引数つきで起動する。起動できたか。</summary>
    private static bool TryLaunchSelf()
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe) return false;

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
            start.ArgumentList.Add(StartupArguments.AfterRestart);
            System.Diagnostics.Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Shell.ShellDiagnosticsLog.Write($"立ち上げ直し: 失敗。{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // データベースを開けなかったとき（要件書 3章・安全装置）
    // ------------------------------------------------------------------

    /// <summary>
    /// データベースを開く。開けなければ、理由に合わせて案内する。
    /// <para>
    /// <b>「ロックされている」と「壊れている」を取り違えない。</b>ほかのプロセスが使っているだけなのに
    /// 「壊れている」扱いにして、健全なデータを退避させてはいけない。
    /// ロック・権限・ディスクの空きは、時間をおいてやり直すか終了を選ばせる（退避は勧めない）。
    /// 本当に壊れているとき（SQLITE_CORRUPT・NOTADB）と新しい版のデータのときだけ、
    /// 「どけて新しく始める」「バックアップから戻す」を選ばせる。
    /// </para>
    /// </summary>
    /// <returns>開けた接続。開けなかった（終了・立ち上げ直しにした）なら null。</returns>
    private SqliteConnection? OpenDatabaseOrAskUser()
    {
        while (true)
        {
            try
            {
                return CalendarDatabase.OpenDefault().ConnectAndMigrate();
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException
                                           or IOException or UnauthorizedAccessException)
            {
                var kind = DatabaseOpenFailure.Classify(ex);

                Shell.ShellDiagnosticsLog.Write($"データベースを開けません（{kind}）。{ex.GetType().Name}: {ex.Message}");

                if (DatabaseOpenFailure.OffersSetAside(kind))
                {
                    HandleBrokenDatabase(kind, ex);
                    return null;
                }

                var answer = Shell.FrontMessageBox.Show(
                    DatabaseOpenFailure.Describe(kind, CalendarDatabase.DefaultPath)
                    + $"\n\n詳細: {ex.Message}\n\n「再試行」: もう一度開きます。「キャンセル」: 終了します。",
                    Shell.FrontButtons.RetryCancel, Shell.FrontIcon.Error);

                if (answer != Shell.FrontResult.Retry)
                {
                    Shutdown(1);
                    return null;
                }

                // 掴んだままのものを手放し、少し間をおいてから開き直す
                SqliteConnection.ClearAllPools();
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>
    /// 壊れている（または新しい版の）データベースの案内。
    /// <para>
    /// 黙って落とすと、利用者には何もできない。せめて「壊れたものをどけて
    /// 新しく始める」「バックアップから戻す」を選べるようにする。
    /// </para>
    /// </summary>
    private void HandleBrokenDatabase(DatabaseOpenFailureKind kind, Exception ex)
    {
        var choice = Shell.FrontMessageBox.Show(
            DatabaseOpenFailure.Describe(kind, CalendarDatabase.DefaultPath) + "\n\n"
            + "「はい」: データをどけて、新しく始めます（今までの予定・タスクは"
            + "戻せなくなりますが、ファイル自体は残るので後から取り出せます）。\n"
            + "「いいえ」: バックアップファイルから復元します。\n"
            + "「キャンセル」: 何もせず終了します。\n\n"
            + $"詳細: {ex.Message}\n保存先: {CalendarDatabase.DefaultPath}",
            Shell.FrontButtons.YesNoCancel, Shell.FrontIcon.Error);

        switch (choice)
        {
            case Shell.FrontResult.Yes:
                SetAsideBrokenDatabaseAndRestart();
                return;

            case Shell.FrontResult.No:
                RestoreFromPickedBackupAndRestart();
                return;

            default:
                Shutdown(1);
                return;
        }
    }

    /// <summary>
    /// 壊れたデータベースをどけて、立ち上げ直す。
    /// <para>
    /// 消すのではなく <c>.broken-日時</c> へ改名する。中身を諦めるのはこちらの判断
    /// だけでは決められないので、あとから利用者が手で取り出せるように残しておく。
    /// </para>
    /// </summary>
    private void SetAsideBrokenDatabaseAndRestart()
    {
        try
        {
            // 接続を使い終わってもプールに残る。どけようとしているファイルを
            // 掴んだままだと、改名も失敗する
            SqliteConnection.ClearAllPools();

            var target = CalendarDatabase.DefaultPath;

            if (File.Exists(target))
            {
                var broken = $"{target}.broken-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
                File.Move(target, broken);
            }

            // 古い WAL が残っていると、次に作る新しいデータベースと食い違う
            foreach (var suffix in (string[])["-wal", "-shm"])
            {
                var side = target + suffix;
                if (File.Exists(side)) File.Delete(side);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.FrontMessageBox.Show(
                $"壊れたデータをどけられませんでした。\n\n{ex.Message}",
                Shell.FrontButtons.Ok, Shell.FrontIcon.Error);
            Shutdown(1);
            return;
        }

        RestartProcess();
    }

    /// <summary>選んだバックアップファイルで置き換えて、立ち上げ直す。</summary>
    private void RestoreFromPickedBackupAndRestart()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "復元するバックアップを選ぶ",
            Filter = "Kado のバックアップ (*.db)|*.db|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            Shutdown(1);
            return;
        }

        try
        {
            DatabaseBackup.RestoreFrom(dialog.FileName, CalendarDatabase.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or FileNotFoundException or SqliteException)
        {
            Shell.FrontMessageBox.Show(
                "復元できませんでした。\n\n"
                + (ex is SqliteException sqlite ? SqliteFailure.DescribeReason(sqlite) : ex.Message),
                Shell.FrontButtons.Ok, Shell.FrontIcon.Error);
            Shutdown(1);
            return;
        }

        RestartProcess();
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
        _syncConnection?.Dispose();
        _instance?.Dispose();

        // 終了の問い合わせを受けていたなら、結果（本当に終わるのか、取り消されたのか）が届くまで
        // 見張りを生かしておく。そうでなければ、ここで止める
        _sessionWatcher?.Dispose();

        base.OnExit(e);
    }

    // ------------------------------------------------------------------
    // シャットダウン・サインアウト
    // ------------------------------------------------------------------

    /// <summary>
    /// 終了の問い合わせ（<c>WM_QUERYENDSESSION</c>）。
    /// <para>
    /// <b>ここでは保存と後片付けの準備だけをする。</b>AppBar を外すのは、実際に終わるとき
    /// （<see cref="OnExit"/>、または見張りが <c>WM_ENDSESSION</c> の wParam=TRUE を受けたとき）。
    /// ここで外すと、他のアプリが取り消したときにワークエリアだけが戻って、常駐が中途半端になる。
    /// </para>
    /// <para>
    /// WPF はこの問い合わせを取り消さない限り、自分で <see cref="Application.Shutdown()"/> を呼ぶ。
    /// 取り消せば Windows のシャットダウンを妨げてしまうので、取り消さない。そのかわり、
    /// 他のアプリが取り消したときは <see cref="SessionEndWatcher"/> が立ち上げ直して常駐を続ける
    /// （<see cref="OnSessionResolved"/>）。
    /// </para>
    /// </summary>
    private void HandleSessionEnding(SessionEndingCancelEventArgs args)
    {
        _sessionEndQueried = true;

        try
        {
            // 居かたを控える（終わるときに DB へ書き出す準備）
            _shellController?.Save();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 保存できなくても、終了の邪魔はしない
        }
    }

    /// <summary>
    /// 終了の結果が届いた（見張りのスレッドから呼ばれる）。
    /// </summary>
    /// <param name="ending">本当に終わるか（<c>WM_ENDSESSION</c> の wParam）。</param>
    private void OnSessionResolved(bool ending)
    {
        if (ending)
        {
            // 本当に終わる。この関数が返ると Windows がプロセスを終わらせるので、
            // AppBar を外すのはここで済ませる（すでに外れていれば何もしない）
            ReleaseShellFromAnyThread();
            return;
        }

        // 他のアプリが取り消した。WPF は問い合わせの時点で終わりにかかっているので、
        // 立ち上げ直して常駐を続ける。問い合わせを受けていなければ（まだ動いているなら）何もしない
        if (_sessionEndQueried && !_reallyExiting)
        {
            Shell.ShellDiagnosticsLog.Write("シャットダウンが取り消されたので、立ち上げ直します");
            TryLaunchSelf();
        }
    }

    // ------------------------------------------------------------------
    // シェル統合（要件書 2章・7章）
    // ------------------------------------------------------------------

    /// <summary>トレイ・ホットキー・居かたの制御を組み立てる。</summary>
    private void SetUpShell(MainWindow window)
    {
        if (window.DataContext is not MainViewModel main || _dockStore is null) return;

        // メニュー・ポップアップの開閉をまとめて数え始める（項目3）。
        // ShellController より前でよい。ContextMenu/Popup がまだ1つも開いて
        // いない起動直後に呼ぶので、順序そのものは問わない
        Shell.PopupActivityHooks.Install();

        _shellController = new Shell.ShellController(window, main.Shell, _dockStore);

        // スライドの引っ込め方と、いちばん細くできる幅は設定から。
        // 変えたらその場で効かせる
        if (_settings is { } settings)
        {
            _shellController.SlideOutOnLeave = settings.SlideOutOnLeave;
            main.Shell.MinWidth = settings.MinWidth;

            settings.Changed += (_, _) =>
            {
                main.Shell.MinWidth = settings.MinWidth;

                if (_shellController is { } controller)
                {
                    controller.SlideOutOnLeave = settings.SlideOutOnLeave;
                }
            };
        }

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
                "Kado", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _shellController.Restore();

        // DB 版の印だけでなく、DB を介さない印も合わせておく。Restore() は直接
        // Apply() を呼ぶので ModeChanged は上がらず、ここで一度明示的に合わせる
        Shell.WorkAreaGuard.MarkReserved(main.Shell.Mode == ShellMode.Dock);

        _tray = new Shell.TrayIcon("Kado", BuildTrayMenu(main));
        _tray.Activated += (_, _) => Dispatcher.Invoke(BringToFront);

        // 他のアプリを使っているあいだでも効かせる。取られていれば黙って諦める
        _hotKeys = Shell.GlobalHotKeys.Attach(window);
        if (_hotKeys is not null) _hotKeys.Pressed += (_, kind) => Dispatcher.Invoke(() => OnHotKey(main, kind));

        // 居かたが変わるたびに控える。終了時だけだと、落ちたときに戻せない
        main.Shell.ModeChanged += (_, _) => _shellController?.Save();
        main.Shell.EdgeChanged += (_, _) => _shellController?.Save();
        main.Shell.DockWidthChanged += (_, _) => _shellController?.Save();

        // ワークエリアを削っている／いないの、DB を介さない印も同じタイミングで
        // 合わせる。データベースが壊れて開けなくなったときの最後の砦になる
        // （WorkAreaGuard.RecoverIfNeeded()、App.OnStartup 側）
        main.Shell.ModeChanged += (_, _) =>
            Shell.WorkAreaGuard.MarkReserved(main.Shell.Mode == ShellMode.Dock);
    }

    /// <summary>
    /// スリープから戻ったとき・時計を変えられたときに、すぐ追いつく（要件書 7.5）。
    /// <para>
    /// 眠っているあいだタイマーは止まっている。起きたあと次の1分を待つと、その間に
    /// 知らせるはずだった予定が遅れる。<see cref="ReminderService"/> は「知らせる時刻を
    /// 過ぎていて、まだ始まっていないもの」を出す作りなので、起こしてやれば取り戻せる。
    /// 日をまたいで眠っていた場合も、「今日」と選択日をすぐ新しい日へ移せる。
    /// </para>
    /// <para>
    /// この知らせは UI のスレッドには来ないので、渡し直してから触る。
    /// <b>どちらも static イベントで、購読したままだとアプリより長く生きて漏れる。</b>
    /// 終了時に必ず外す。
    /// </para>
    /// </summary>
    private void WatchForResume(MainWindow window)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;
        Exit += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        };

        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs args) =>
            Recheck(args.Mode switch
            {
                Microsoft.Win32.PowerModes.Resume => ClockChange.Resume,
                Microsoft.Win32.PowerModes.Suspend => ClockChange.Suspend,
                _ => ClockChange.PowerStatus,
            });

        void OnTimeChanged(object? sender, EventArgs args) => Recheck(ClockChange.TimeChanged);

        void Recheck(ClockChange change)
        {
            if (!ClockChangeRules.ShouldRecheck(change)) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (window.DataContext is MainViewModel main) main.UpdateNow(DateTime.Now);
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

        // 出しかたと、出す位置を分ける。ツールバーのボタンと同じ考え方で、
        // 上の3つが「どう出すか」、下の2つが「どちらの端から出すか」
        Add("ウィンドウ", () => main.Shell.ToWindowCommand.Execute(null));
        Add("スライド", () => main.Shell.ToOverlayCommand.Execute(null));
        Add("出したまま固定する・やめる", () => main.Shell.TogglePinCommand.Execute(null));
        menu.Items.Add(new System.Windows.Controls.Separator());

        Add("左端から出す", () => main.Shell.EdgeLeftCommand.Execute(null));
        Add("右端から出す", () => main.Shell.EdgeRightCommand.Execute(null));
        menu.Items.Add(new System.Windows.Controls.Separator());

        Add("今すぐ同期", () => main.Sync.SyncNowCommand.Execute(null));
        Add("ショートカット", () => main.ShowShortcutsCommand.Execute(null));
        Add("設定", () => main.OpenSettingsCommand.Execute(null));
        menu.Items.Add(new System.Windows.Controls.Separator());

        // ここでだけ本当に終わる。閉じるボタンはトレイに入るだけ
        Add("終了", () => { _reallyExiting = true; Shutdown(); });

        return menu;
    }

    private void OnHotKey(MainViewModel main, Shell.HotKeyKind kind)
    {
        switch (kind)
        {
            // 画面端に留めると枠が消える。ここが最後の戻り口になるので、
            // 前に出すより先に外す
            case Shell.HotKeyKind.Pin:
                main.Shell.TogglePinCommand.Execute(null);
                return;

            case Shell.HotKeyKind.Slide:
                main.Shell.ToggleSlideCommand.Execute(null);
                return;
        }

        BringToFront();

        // クイック入力の欄へ飛ばす。呼び出してから手で探させない
        if (kind == Shell.HotKeyKind.QuickEntry) (MainWindow as MainWindow)?.FocusQuickInput();
    }

    /// <summary>
    /// 閉じるボタンではトレイに入るだけにする（要件書 7.4）。
    /// <para>終了はトレイのメニューから。更新のための終了はここを通らない。</para>
    /// </summary>
    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // トレイにアイコンが出ていないあいだは隠さない。隠すと、窓を呼び戻す入口がどこにも無い。
        // （Explorer の再起動やログオン直後の失敗で出ていないときは、そのまま閉じる＝終了する。
        // 見えない常駐を残すよりよい）
        var closeToTray = _settings is not { CloseToTray: false };

        if (!Shell.TrayPolicy.HidesOnClose(_reallyExiting, _tray?.IsShown == true, closeToTray)) return;

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
    /// <summary>
    /// どのスレッドからでも、ワークエリアを元に戻す。
    /// <para>
    /// <see cref="AppDomain.UnhandledException"/> は、落ちたスレッド（多くは画面以外）で来る。
    /// <c>ReleaseShell</c> は画面のスレッドのもの（AppBar と窓）を触るので、画面のスレッドへ渡して
    /// <b>同期的に</b>行う。終わるのを待たないと、渡した処理が走る前にプロセスが終わる。
    /// ただし画面のスレッドも止まっている（デッドロックなど）ことがあるので、<b>待つのは3秒まで</b>。
    /// 間に合わなかったときは、次の起動で <c>WorkAreaGuard</c> が戻す（終了印が残っている）。
    /// </para>
    /// </summary>
    private void ReleaseShellFromAnyThread()
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                ReleaseShell();
                return;
            }

            Dispatcher.Invoke(
                ReleaseShell, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 画面のスレッドがもう無い（終わった）、渡せなかった。ここでの失敗は、元の落ちた理由を隠さない
        }
    }

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
