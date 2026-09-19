using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 月ビュー。表示だけを担い、状態は <see cref="MonthViewModel"/> が持つ。
/// <para>
/// マスを押したときの動きだけはここで受ける。<c>InputBinding</c> からは
/// <c>RelativeSource</c> で祖先のウィンドウをたどれないため。
/// </para>
/// </summary>
public partial class MonthView : UserControl
{
    public MonthView() => InitializeComponent();

    /// <summary>1回押しでその日を選び、2回でその日に予定を足す。</summary>
    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayCellViewModel cell) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2) main.AddEventOnCommand.Execute(cell.Date);
        else main.SelectDateCommand.Execute(cell.Date);

        e.Handled = true;
    }

    /// <summary>
    /// マスに並ぶ予定を2回押すとその予定を開く。
    /// <para>
    /// ここで止めないとマス側の受け手に流れ、その日に新しい予定を足すことになる。
    /// 1回押しはマスと同じでその日を選ぶ。
    /// </para>
    /// </summary>
    private void OnEventChipClicked(object sender, MouseButtonEventArgs e) =>
        Handle(sender, e, (main, item) => main.EditChipCommand.Execute(item));

    /// <inheritdoc cref="OnEventChipClicked"/>
    private void OnTaskChipClicked(object sender, MouseButtonEventArgs e) =>
        Handle(sender, e, (main, item) => main.EditTaskChipCommand.Execute(item));

    private void Handle(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if ((sender as FrameworkElement)?.DataContext is not { } item) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2) open(main, item);
        else if (FindCell(sender as DependencyObject) is { } cell) main.SelectDateCommand.Execute(cell.Date);

        e.Handled = true;
    }

    /// <summary>その予定が乗っているマス。日を選ぶのに要る。</summary>
    private static DayCellViewModel? FindCell(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: DayCellViewModel cell }) return cell;
        }

        return null;
    }
}
