using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 画面の隅に出す知らせ。数秒で消える。
/// <para>押すと本体を前に出す。閉じるボタンでも消える。</para>
/// </summary>
public partial class ToastWindow : Window
{
    /// <summary>いま出ているもの。下から積み上げるために控える。</summary>
    private static readonly List<ToastWindow> Shown = [];

    private readonly DispatcherTimer _life = new() { Interval = TimeSpan.FromSeconds(10) };

    public ToastWindow(string title, string message)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;

        _life.Tick += (_, _) => Close();

        Loaded += (_, _) =>
        {
            Place();
            _life.Start();
        };

        // 高さは中身で決まる。決まってから置き直さないと、画面の外にはみ出す
        SizeChanged += (_, _) => Place();

        Closed += (_, _) =>
        {
            _life.Stop();
            Shown.Remove(this);
            Restack();
        };
    }

    /// <summary>知らせを出す。</summary>
    public static void Show(string title, string message)
    {
        var toast = new ToastWindow(title, message);
        Shown.Add(toast);

        // 触らせない。入力を奪うと、打っている最中に文字が飛ぶ
        toast.Show();
    }

    /// <summary>右下に置く。すでに出ているものがあれば、その上に積む。</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;

        Left = area.Right - Width;
        Top = area.Bottom - OffsetOf(Shown.IndexOf(this)) - ActualHeight;
    }

    /// <summary>1つ消えたら、残りを詰め直す。</summary>
    private static void Restack()
    {
        var area = SystemParameters.WorkArea;

        for (var i = 0; i < Shown.Count; i++)
        {
            Shown[i].Top = area.Bottom - OffsetOf(i) - Shown[i].ActualHeight;
        }
    }

    private static double OffsetOf(int index) => Math.Max(index, 0) * StackStep;

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (Application.Current?.MainWindow is { } main)
        {
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;

            main.Activate();
        }

        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    /// <summary>積むときの間隔。小窓の高さは中身で変わるので、ゆとりを見た固定値にする。</summary>
    private const double StackStep = 96;
}
