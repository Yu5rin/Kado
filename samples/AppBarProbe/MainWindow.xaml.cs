using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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

        if (App.PendingRecovery is { } pending)
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Log(WorkAreaRecovery.Recover(pending, hwnd));
        }
        else
        {
            // 何も起きなかったことを明示しておく。検証中に「検知されたのか判断できない」を避ける。
            Log("前回は正常に終了しています（復旧の必要はありません）。");
            Log($"控えの保存先: {WorkAreaRecovery.StateFilePath}");
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

    /// <summary>
    /// 例外で AppBar が緊急解除されたあとの後始末。
    /// <para>
    /// 緊急解除は UI に触れないため、枠を外したままピンボタンも押された状態で残る。
    /// 続行できる例外ではここで見た目を揃えておかないと、ウィンドウを閉じられなくなる。
    /// </para>
    /// </summary>
    internal void ResetAfterEmergency()
    {
        _appBar.RestoreChromeIfNeeded();

        // Unchecked が走り、ボタンの表示と入力欄の有効・無効が元に戻る。
        // AppBar は解除済みなので Unregister は何もしない。
        PinButton.IsChecked = false;

        Log("例外により AppBar を解除しました。ウィンドウの表示を元に戻しています。");
    }

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
