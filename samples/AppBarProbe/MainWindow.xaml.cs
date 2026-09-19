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
    /// <summary>復旧の検証用に、わざと削るワークエリアの幅。</summary>
    private const int BrokenWidth = 352;

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
        _appBar.EdgeOverlap = ParseOverlap();

        _appBar.Register();

        PinButton.Content = "ピンを外す（AppBar 解除）";
        EdgeBox.IsEnabled = false;
        WidthBox.IsEnabled = false;
        OverlapBox.IsEnabled = false;

        Log($"登録後のワークエリア: {NativeMethods.GetWorkArea()}");
    }

    private void OnPinUnchecked(object sender, RoutedEventArgs e)
    {
        _appBar.Unregister();

        PinButton.Content = "ピン留めする（AppBar 登録）";
        EdgeBox.IsEnabled = true;
        WidthBox.IsEnabled = true;
        OverlapBox.IsEnabled = true;

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
    /// 安全装置3だけを単独で確かめる。
    /// <para>
    /// 強制終了しても Windows 側が AppBar の登録を掃除してワークエリアを戻すことがあり、
    /// その場合「削られたまま残る」状況を再現できない。復旧処理が本当に効くのかを
    /// 確かめられないので、壊れた状態を意図的に作れるようにしてある。
    /// </para>
    /// </summary>
    private void OnBreakClick(object sender, RoutedEventArgs e)
    {
        if (_appBar.IsRegistered)
        {
            Log("先にピンを外してください。ピン留め中は実行できません。");
            return;
        }

        var current = NativeMethods.GetWorkArea();

        if (!WorkAreaRecovery.MarkRegistered(current))
        {
            Log("控えを保存できませんでした。この状態で削ると戻せないので中止します。");
            return;
        }

        // 異常終了でワークエリアが削られたまま残った状態を作る
        var broken = current with { Left = current.Left + BrokenWidth };
        NativeMethods.SetWorkArea(broken);

        Log($"ワークエリアを {current} → {NativeMethods.GetWorkArea()} に削り、控えを残しました。");
        Log("このままアプリを終了し、もう一度起動してください。復旧すれば安全装置3は正しく効いています。");
    }

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

    /// <summary>
    /// 隣のウィンドウとの隙間を埋める量。0 なら埋めない。
    /// 影のマージンは通常 10px 程度なので、それを超える値は受け付けない。
    /// </summary>
    private int ParseOverlap()
    {
        if (int.TryParse(OverlapBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is >= 0 and <= 32)
        {
            return v;
        }

        Log($"隙間埋めの指定が不正です（'{OverlapBox.Text}'）。0 として扱います。");
        OverlapBox.Text = "0";
        return 0;
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();
    }
}
