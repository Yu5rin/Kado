using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AppBarProbe.Interop;

namespace AppBarProbe;

/// <summary>
/// 検証用の画面。ボタンひとつで AppBar の登録／解除を切り替える。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppBarController _appBar;
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();

        _appBar = new AppBarController(this);
        _appBar.StatusChanged += Log;
        App.ActiveController = _appBar;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log($"起動しました。現在のワークエリア: {NativeMethods.GetWorkArea()}");

        if (App.RecoveryMessage is { } message)
        {
            Log(message);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 正常終了の経路。ここで必ず解除する。
        _appBar.Dispose();
    }

    // ------------------------------------------------------------------

    private void OnPinChecked(object sender, RoutedEventArgs e)
    {
        _appBar.Edge = EdgeBox.SelectedIndex == 0 ? AppBarEdge.Left : AppBarEdge.Right;
        _appBar.DesiredWidth = ParseWidth();

        _appBar.Register();

        PinButton.Content = "ピンを外す（AppBar 解除）";
        EdgeBox.IsEnabled = false;
        WidthBox.IsEnabled = false;

        Log($"登録後のワークエリア: {NativeMethods.GetWorkArea()}");
    }

    private void OnPinUnchecked(object sender, RoutedEventArgs e)
    {
        _appBar.Unregister();

        PinButton.Content = "ピン留めする（AppBar 登録）";
        EdgeBox.IsEnabled = true;
        WidthBox.IsEnabled = true;

        Log($"解除後のワークエリア: {NativeMethods.GetWorkArea()}");
    }

    private void OnEdgeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !_appBar.IsRegistered) return;

        _appBar.Edge = EdgeBox.SelectedIndex == 0 ? AppBarEdge.Left : AppBarEdge.Right;
        _appBar.Reposition();
    }

    private void OnCrashClick(object sender, RoutedEventArgs e)
        => throw new InvalidOperationException("安全装置の検証のために投げた例外です。");

    // ------------------------------------------------------------------

    private int ParseWidth()
    {
        if (int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && width is >= 120 and <= 1200)
        {
            return width;
        }

        Log($"幅の指定が不正です（'{WidthBox.Text}'）。既定の 352px を使います。");
        WidthBox.Text = "352";
        return 352;
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();
    }
}
