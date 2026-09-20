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
    public SidebarLayout() => InitializeComponent();

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
