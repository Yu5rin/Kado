using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 年ビュー。
/// <para>日を押したら中央の選択を合わせる。ホイールで年度を前後に送る。</para>
/// </summary>
public partial class YearView : UserControl
{
    public YearView() => InitializeComponent();

    /// <summary>日を押したら選択を合わせる。ダブルクリックでその日の月ビューへ。</summary>
    private void OnDayClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YearDayViewModel day) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.SelectedDate = day.Date;

        // 年から日付を選んだあとは、たいていその月を見たい
        if (e.ClickCount == 2) main.CurrentView = CalendarView.Month;

        e.Handled = true;
    }

    /// <summary>
    /// ホイールで年度を送る。
    /// <para>
    /// 月・週・日ビューと同じで、そのまま回せば前後へ動く。はじめは Ctrl を
    /// 押させていたが、他のビューと違うので押す理由が伝わらなかった。
    /// </para>
    /// <para>
    /// 12か月ぶんは画面に収まる作りなので、スクロールを譲る必要もない。収まらない
    /// ときはスクロールバーで動かす。
    /// </para>
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not YearViewModel year) return;

        if (e.Delta > 0) year.GoToPreviousYear();
        else year.GoToNextYear();

        e.Handled = true;
    }
}
