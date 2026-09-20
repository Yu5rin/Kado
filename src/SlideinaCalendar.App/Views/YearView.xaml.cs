using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 年ビュー。
/// <para>
/// 日を押したら中央の選択を合わせる。ホイールで年度を前後に送る。
/// マスの幅は画面の幅から決める。
/// </para>
/// </summary>
public partial class YearView : UserControl
{
    /// <summary>左の月名と、右の実働日数に取ってある幅。残りを31日で割る。</summary>
    private const double SideRoom = 46 + 60 + 24;

    /// <summary>カレンダー表示の1枚ぶんの幅（外側の余白を含む）。</summary>
    private const double GridCardWidth = 202;

    /// <summary>縦のスクロールバーが出たぶん。出てから測ると幅が揺れる</summary>
    private const double ScrollRoom = 14;

    public YearView() => InitializeComponent();

    /// <summary>日を押したら選択を合わせる。ダブルクリックでその日に予定を足す。</summary>
    private void OnDayClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YearDayViewModel day) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        // 月ビューのマスと同じ。1回押しで選び、2回で予定を足す
        if (e.ClickCount == 2) main.AddEventOnCommand.Execute(day.Date);
        else main.SelectDateCommand.Execute(day.Date);

        e.Handled = true;
    }

    /// <summary>
    /// ホイールで年度を送る。
    /// <para>月・週・日と同じで、そのまま回せば前後へ動く。</para>
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not YearViewModel year) return;

        // Ctrl はビューの切り替えに使う。ここでは受けない
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        if (e.Delta > 0) year.GoToPreviousYear();
        else year.GoToNextYear();

        e.Handled = true;
    }

    /// <summary>
    /// 幅に合わせて縮尺を決める。
    /// <para>
    /// 固定だと、広い画面では右が余り、狭い画面では横のスクロールバーが出る。
    /// カレンダー表示のほうは、入る枚数から列数を決める。
    /// </para>
    /// </summary>
    private void OnResized(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not YearViewModel year) return;

        var room = e.NewSize.Width - SideRoom - ScrollRoom;

        // マスとマスのあいだに 1px 空けてある
        year.DayWidth = room > 0 ? (room / 31) - 1 : YearViewModel.DefaultDayWidth;

        // 12枚を何列で並べるか。4列に収まらなければ減らす
        var fits = (int)(e.NewSize.Width / GridCardWidth);
        year.GridColumns = Math.Clamp(fits, 1, 4);
    }
}
