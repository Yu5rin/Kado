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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var window = new MainWindow
        {
            DataContext = new MainViewModel(workspace, today),
        };

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _connection?.Dispose();
        base.OnExit(e);
    }
}
