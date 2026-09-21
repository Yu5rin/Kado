using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// サイドバーモードの中身。
/// <para>データの出どころは3ペインと同じ <c>MainViewModel</c>。形だけを変えている。</para>
/// </summary>
public partial class SidebarLayout : UserControl
{
    public SidebarLayout()
    {
        InitializeComponent();

        // 仕切りの位置は覚えておく。カレンダーを広く見たい人と、予定の一覧を
        // 長く出したい人がいる
        Loaded += (_, _) => RestoreShare();
        Split.DragCompleted += (_, _) => SaveShare();
    }

    /// <summary>控えてある割り振りに戻す。</summary>
    private void RestoreShare()
    {
        if (DataContext is not MainViewModel vm) return;

        var share = vm.SlimCalendarShare;

        CalendarRow.Height = new GridLength(share, GridUnitType.Star);
        ListRow.Height = new GridLength(1 - share, GridUnitType.Star);
    }

    /// <summary>いまの割り振りを控える。</summary>
    private void SaveShare()
    {
        if (DataContext is not MainViewModel vm) return;

        var total = CalendarRow.ActualHeight + ListRow.ActualHeight;

        if (total <= 0) return;

        vm.SlimCalendarShare = CalendarRow.ActualHeight / total;
    }

    /// <summary>クイック入力は Enter で入れる。</summary>
    private void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel vm || !vm.QuickCommand.CanExecute(null)) return;

        vm.QuickCommand.Execute(null);
        e.Handled = true;
    }

    private void OnEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditEventCommand.Execute(item));

    private void OnTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskCommand.Execute(item));

    /// <summary>ダブルクリックで編集。1回押しは選ぶだけにする。</summary>
    private static void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement element || element.DataContext is not { } item) return;
        if (Window.GetWindow(element)?.DataContext is not MainViewModel main) return;

        open(main, item);
        e.Handled = true;
    }
}
