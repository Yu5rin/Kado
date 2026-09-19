using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App;

/// <summary>
/// ウィンドウモードの本体。状態は <see cref="MainViewModel"/> が持つ。
/// <para>
/// ここに書いてあるのはダブルクリックの受け口だけ。<c>InputBinding</c> は視覚ツリーに
/// 居ないため <c>RelativeSource</c> で祖先をたどれず、XAML だけでは繋げられない。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>現在時刻の線を動かす時計。1分ごとで足りる。</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => ViewModel?.UpdateNow(DateTime.Now);

        // 出した直後に一度合わせる。1分待たないと線が出ないのを避ける
        Loaded += (_, _) =>
        {
            ViewModel?.UpdateNow(DateTime.Now);
            _clock.Start();
        };

        Closed += (_, _) => _clock.Stop();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>右ペインの予定。ダブルクリックで編集画面を開く。</summary>
    private void OnEventRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DataContextOf<DayEventViewModel>(sender) is not { } target) return;

        ViewModel?.EditEventCommand.Execute(target);
        e.Handled = true;
    }

    /// <summary>右ペインのタスク。ダブルクリックで編集画面を開く。</summary>
    private void OnTaskRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DataContextOf<TaskListItemViewModel>(sender) is not { } target) return;

        ViewModel?.EditTaskCommand.Execute(target);
        e.Handled = true;
    }

    private static T? DataContextOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;
}
