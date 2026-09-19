using System.Windows;
using System.Windows.Controls;

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

    public TimelineColumnView() => InitializeComponent();

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
}
