using System.Windows;
using System.Windows.Controls;
using SlideinaCalendar.Data.Models;
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

    /// <summary>時間軸の上端の時。落とした場所から時刻を出すのに要る。</summary>
    public static readonly DependencyProperty DayStartHourProperty = DependencyProperty.Register(
        nameof(DayStartHour), typeof(int), typeof(TimelineColumnView), new PropertyMetadata(0));

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

    /// <inheritdoc cref="DayStartHourProperty"/>
    public int DayStartHour
    {
        get => (int)GetValue(DayStartHourProperty);
        set => SetValue(DayStartHourProperty, value);
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

    // ------------------------------------------------------------------
    // ドラッグで時刻ごと動かす
    //
    // 月ビューは日だけを変えるが、こちらは落とした高さが時刻になる。
    // 15分きざみに丸める。1分単位で置けても、狙って置けるものではない
    // ------------------------------------------------------------------

    /// <summary>
    /// 落とした高さを時刻に直す。15分きざみ。
    /// <para>
    /// <b>出している時間帯の中に収める。</b>外に出すと、移した先が画面から見えなくなり、
    /// 予定が消えたように見える。
    /// </para>
    /// </summary>
    internal TimeOnly TimeAt(double y)
    {
        var first = Math.Clamp(DayStartHour, 0, 23) * 60;
        if (RowHeight <= 0) return new TimeOnly(first / 60, 0);

        // 出している最後の時間の頭まで。1日ぶん出しているなら 23:45 まで
        var last = Math.Min(first + (Math.Max(HourCount, 1) * 60) - 15, (24 * 60) - 15);

        var minutes = first + (y / RowHeight * 60);
        var snapped = Math.Round(minutes / 15, MidpointRounding.AwayFromZero) * 15;
        var clamped = (int)Math.Clamp(snapped, first, last);

        return new TimeOnly(clamped / 60, clamped % 60);
    }

    /// <summary>出している時間の数。時刻の見出しの数で分かる。</summary>
    private int HourCount => HourLabels is System.Collections.ICollection labels ? labels.Count : 24;

    /// <summary>押したまま動かしたらドラッグを始める。</summary>
    private void OnBlockDragging(object sender, System.Windows.Input.MouseEventArgs e) =>
        DragSession.Current.DragIfMoved(sender, e);

    /// <summary>列の上を通っているあいだ。落とせるかどうかをカーソルで示す。</summary>
    private void OnColumnDragOver(object sender, DragEventArgs e) =>
        DragSession.ShowEffect(e, DataContext is WeekDayColumnViewModel && DragSession.Payload(e) is not null);

    /// <summary>落とされたら、その日のその時刻へ移す。</summary>
    private void OnColumnDropped(object sender, DragEventArgs e)
    {
        if (DataContext is not WeekDayColumnViewModel column) return;

        e.Handled = true;

        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        var at = TimeAt(e.GetPosition(this).Y);
        var copy = DragSession.IsCopy(e);

        switch (DragSession.Payload(e))
        {
            case EventChipViewModel chip:
                main.MoveEventToTime(chip.Id, column.Date, at, copy);
                break;

            // 作業時間ブロックはタスクのもの。動かすのは予定だけ
            case TimeBlockViewModel { IsWorkBlock: false } block:
                main.MoveEventToTime(block.Id, column.Date, at, copy);
                break;

            // 日付の行のラベルは時刻を持たない。日だけを移す
            case MilestoneViewModel milestone:
                main.MoveEventTo(milestone.Id, column.Date, copy);
                break;

            // タスクが持つのは期限で、時刻は持たない
            case TaskItem task:
                main.MoveTaskTo(task.Id, column.Date, copy);
                break;
        }
    }

    /// <summary>
    /// 時間軸の1件を2回押すと開く。
    /// <para>作業時間ブロックは予定ではないので、もとになったタスクのほうが開く。</para>
    /// </summary>
    private void OnBlockClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 1回押しはドラッグの始まり
        DragSession.Current.Press(sender, e);

        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not TimeBlockViewModel block) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.EditBlockCommand.Execute(block);
        e.Handled = true;
    }
}
