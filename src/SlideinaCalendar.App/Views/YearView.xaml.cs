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

    /// <summary>
    /// カレンダー表示を4列に並べたいときの、1枚ぶんの最小の幅。
    /// <para>
    /// 1枚の中身は Viewbox で入れ物に合わせて伸び縮みするので、素の 202px より
    /// 狭くても読める。四半期が縦に揃う並びを崩したくないので、ここは低めに取る。
    /// </para>
    /// </summary>
    private const double GridCardWidth = 160;

    /// <summary>カレンダー1枚を、これより低くはしない。下回るぶんはスクロールさせる。</summary>
    private const double GridCardMinHeight = 150;

    /// <summary>ScrollViewer の Padding="12,10" のうち、上下ぶん。</summary>
    private const double ScrollPadding = 20;

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
        if (e.Delta == 0) return;

        // Ctrl はビューの切り替えに使う。ここでは受けない
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        // 年ビューに直に頼まない。ツールバーの見出しは MainViewModel が出しているので、
        // 素通りするとツールバーが「2026 年度」のまま中身だけ 2027 年度になる
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        var command = e.Delta > 0 ? main.PreviousCommand : main.NextCommand;
        if (!command.CanExecute(null)) return;

        command.Execute(null);
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

        // 高さを渡さないと、12枚が上に寄ったまま下が余る。ScrollViewer は
        // 中身に高さを聞くので、ここで見えている高さを教えてやる必要がある。
        // 低すぎるときだけ、はみ出したぶんをスクロールさせる
        var height = e.NewSize.Height - ScrollPadding;
        GridHost.Height = Math.Max(year.GridRows * GridCardMinHeight, height);
    }
}
