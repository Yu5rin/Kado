using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.App.Editing;
using SlideinaCalendar.App.Google;
using SlideinaCalendar.App.Themes;
using SlideinaCalendar.Data;
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

    /// <summary>異常終了の記録先。データベースと同じ場所に置く。</summary>
    private static string CrashLogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 2本動くと同期が壊れる。同じデータベースを開き、同じカレンダーへ書き戻すため
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // すでに動いているほうを前に出して、こちらは静かに終わる
            SingleInstance.AskRunningInstanceToShow();
            Shutdown();
            return;
        }

        // 拾わないと OS の「動作を停止しました」だけが出て、理由が何も残らない
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        // 配色を当てるのはウィンドウを作る前。あとから当てると一瞬ちらつく
        ThemeManager.Apply(AppTheme.Auto);

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

            window = new MainWindow
            {
                DataContext = new MainViewModel(
                    workspace, today, editors: editors, files: files,
                    googleClient: googleClient, google: _google),
            };

            MainWindow = window;
            window.Show();

            // 2本目が起動されたら、こちらを前に出す
            _instance.ListenForActivation(() => Dispatcher.Invoke(BringToFront));

            // 裏でも静かに同期する。押し忘れても、開いている間は追いついていく
            if (window.DataContext is MainViewModel main)
            {
                _background = new BackgroundSync(
                    token => Dispatcher.InvokeAsync(
                        () => main.Sync.SyncQuietlyAsync(token)).Task.Unwrap());

                _background.Start();
            }
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
    private static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true,
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _background?.Dispose();
        _google?.Dispose();
        _connection?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
