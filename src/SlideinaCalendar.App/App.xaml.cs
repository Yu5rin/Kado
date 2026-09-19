using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.App.Themes;
using SlideinaCalendar.Data;
using SlideinaCalendar.Presentation;
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

    /// <summary>異常終了の記録先。データベースと同じ場所に置く。</summary>
    private static string CrashLogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            var window = new MainWindow
            {
                DataContext = new MainViewModel(workspace, today),
            };

            MainWindow = window;
            window.Show();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _connection?.Dispose();
        base.OnExit(e);
    }
}
