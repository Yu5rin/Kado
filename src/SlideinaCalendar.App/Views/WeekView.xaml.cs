using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>週ビュー。表示だけを担い、状態は <c>WeekViewModel</c> が持つ。</summary>
public partial class WeekView : UserControl
{
    public WeekView() => InitializeComponent();

    /// <summary>終日レーンの予定を2回押すと開く。</summary>
    private void OnAllDayEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditChipCommand.Execute(item as EventChipViewModel));

    /// <summary>終日レーンのタスクを2回押すと開く。1回押しはドラッグの始まりなので触らない。</summary>
    private void OnAllDayTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskChipCommand.Execute(item as TaskItem));

    private void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not { } item) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        open(main, item);
        e.Handled = true;
    }

    /// <summary>日付の行のラベルを2回押すと、その予定を開く。</summary>
    private void OnMilestoneClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not MilestoneViewModel milestone) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

    /// <summary>
    /// 時間軸に使える高さが変わったら、1時間の高さを決め直す。
    /// <para>
    /// 選んだ時間帯を縦いっぱいに割り付ける。高さは表示側にしか分からないので、
    /// ここで測って ViewModel へ渡す（月ビューのマスと同じ考え方）。
    /// </para>
    /// </summary>
    private void OnTimelineResized(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;
        if (DataContext is WeekViewModel week) week.ViewportHeight = e.NewSize.Height;
    }
}
