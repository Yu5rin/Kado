using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

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

    /// <summary>
    /// カレンダー1枚を、これより低くはしない。下回るぶんはスクロールさせる。
    /// <para>
    /// 曜日見出しの行と、月初の曜日を揃えるための空きマスぶんで6行に固定した
    /// (<see cref="YearViewModel.MonthGridRows"/>)。5行だった頃より高さが要る。
    /// </para>
    /// </summary>
    private const double GridCardMinHeight = 190;

    /// <summary>ScrollViewer の Padding="12,10" のうち、上下ぶん。</summary>
    private const double ScrollPadding = 20;

    /// <summary>
    /// ストリップで、12行の外側に要る高さ。
    /// <para>目盛りの行、「上期」「下期」の見出し2つ、そのあいだの余白ぶん。</para>
    /// </summary>
    private const double StripChrome = 96;

    /// <summary>ストリップの行数。上期6か月＋下期6か月。</summary>
    private const int StripRows = 12;

    /// <summary>ストリップの行と行のあいだ。</summary>
    private const double StripRowGap = 5;

    /// <summary>縦のスクロールバーが出たぶん。出てから測ると幅が揺れる</summary>
    private const double ScrollRoom = 14;

    /// <summary>
    /// 手が止まってから組み直す。
    /// <para>
    /// 12か月ぶん、372 個のマスが幅に合わせて動く。ドラッグのあいだ毎回やると
    /// 画面が固まる。止まってから1回だけにする。
    /// </para>
    /// </summary>
    private readonly Settle _settle;

    /// <summary>落ち着いたときに測る大きさ。</summary>
    private Size _room;

    public YearView()
    {
        InitializeComponent();

        _settle = new Settle(Fit);
    }

    /// <summary>
    /// 日を押したら選択を合わせる。ダブルクリックでその日に予定を足す。
    /// <para>
    /// 実働日計算パネルが開いているあいだは、1回押しをその「から」「まで」にも流す
    /// （月ビューのマスと同じ。要件書 4.5）。
    /// </para>
    /// </summary>
    private void OnDayClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not YearDayViewModel day) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        // 月ビューのマスと同じ。1回押しで選び、2回で予定を足す
        if (e.ClickCount == 2)
        {
            main.AddEventOnCommand.Execute(day.Date);
        }
        else
        {
            main.SelectDateCommand.Execute(day.Date);

            if (main.IsWorkdayCalculatorOpen)
            {
                main.FeedWorkdayCalculator(day.Date, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
        }

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
        _room = e.NewSize;
        _settle.Poke();
    }

    /// <summary>落ち着いた大きさに合わせる。</summary>
    private void Fit()
    {
        if (DataContext is not YearViewModel year || _room.Width <= 0) return;

        var room = _room.Width - SideRoom - ScrollRoom;

        // マスとマスのあいだに 1px 空けてある。
        // 1px 単位に丸めるのは、端数のままだと動かすたびに 372 個の幅が
        // すべて引き直されるため。目で見て違いは出ない
        year.DayWidth = room > 0
            ? Math.Round((room / 31) - 1)
            : YearViewModel.DefaultDayWidth;

        // 12枚を何列で並べるか。4列に収まらなければ減らす
        var fits = (int)(_room.Width / GridCardWidth);
        year.GridColumns = Math.Clamp(fits, 1, 4);

        // 高さを渡さないと、12枚が上に寄ったまま下が余る。ScrollViewer は
        // 中身に高さを聞くので、ここで見えている高さを教えてやる必要がある。
        // 低すぎるときだけ、はみ出したぶんをスクロールさせる
        var height = _room.Height - ScrollPadding;
        GridHost.Height = Math.Max(year.GridRows * GridCardMinHeight, height);

        // ストリップも同じ。12行しか無いので、余ったぶんは行の高さに配る
        var rows = height - StripChrome;
        year.DayHeight = rows > 0
            ? Math.Round((rows / StripRows) - StripRowGap)
            : YearViewModel.MinDayHeight;
    }
}
