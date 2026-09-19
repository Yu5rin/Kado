using System.Windows;
using System.Windows.Controls;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 時間軸の1列。DataContext は <c>WeekDayColumnViewModel</c>。
/// <para>
/// 時刻の見出しは週ビューと日ビューが持っているので、罫線の本数を合わせるために
/// 外から受け取る。親をたどると、週ビューと日ビューで階層が変わってしまう。
/// </para>
/// </summary>
public partial class TimelineColumnView : UserControl
{
    public static readonly DependencyProperty HourLabelsProperty = DependencyProperty.Register(
        nameof(HourLabels), typeof(System.Collections.IEnumerable), typeof(TimelineColumnView),
        new PropertyMetadata(null));

    /// <summary>1時間分の高さ。週ビューは 44、日ビューは 56（モックの寸法）。</summary>
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(TimelineColumnView), new PropertyMetadata(44d));

    /// <summary>今の時刻の位置（時間軸の上端からの距離）。</summary>
    public static readonly DependencyProperty NowOffsetProperty = DependencyProperty.Register(
        nameof(NowOffset), typeof(double), typeof(TimelineColumnView), new PropertyMetadata(0d));

    /// <summary>今の時刻を出すか。表示時間帯の外に出ているときは出さない。</summary>
    public static readonly DependencyProperty ShowNowProperty = DependencyProperty.Register(
        nameof(ShowNow), typeof(bool), typeof(TimelineColumnView), new PropertyMetadata(false));

    public TimelineColumnView() => InitializeComponent();

    /// <inheritdoc cref="NowOffsetProperty"/>
    public double NowOffset
    {
        get => (double)GetValue(NowOffsetProperty);
        set => SetValue(NowOffsetProperty, value);
    }

    /// <inheritdoc cref="ShowNowProperty"/>
    public bool ShowNow
    {
        get => (bool)GetValue(ShowNowProperty);
        set => SetValue(ShowNowProperty, value);
    }

    /// <summary>1時間分の高さ。罫線の間隔になる。</summary>
    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>時刻の見出し。数だけ使い、1時間ぶんの罫線を引く。</summary>
    public System.Collections.IEnumerable? HourLabels
    {
        get => (System.Collections.IEnumerable?)GetValue(HourLabelsProperty);
        set => SetValue(HourLabelsProperty, value);
    }

    /// <summary>
    /// 時間軸の1件を2回押すと開く。
    /// <para>作業時間ブロックは予定ではないので、もとになったタスクのほうが開く。</para>
    /// </summary>
    private void OnBlockClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not TimeBlockViewModel block) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.EditBlockCommand.Execute(block);
        e.Handled = true;
    }
}
