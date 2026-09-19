using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
