using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 一覧ビュー。
/// <para>予定・タスクはダブルクリックで編集、右クリックでメニュー。</para>
/// </summary>
public partial class AgendaView : UserControl
{
    public AgendaView() => InitializeComponent();

    private void OnEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditEventCommand.Execute(item));

    private void OnTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskCommand.Execute(item));

    private static void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not { } item) return;
        if (Window.GetWindow(element)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2)
        {
            open(main, item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// ホイールで期間を送る。
    /// <para>
    /// 縦に長いので、ふつうに回せば中身が動く。Ctrl を押しているあいだだけ
    /// 前後の期間へ移る（年ビューと同じ決まり）。
    /// </para>
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not AgendaViewModel agenda) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        if (e.Delta > 0) agenda.GoToPrevious();
        else agenda.GoToNext();

        e.Handled = true;
    }
}
