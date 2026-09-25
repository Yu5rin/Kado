using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>週ビュー。表示だけを担い、状態は <c>WeekViewModel</c> が持つ。</summary>
public partial class WeekView : UserControl
{
    /// <summary>
    /// 手が止まってから1時間の高さを決め直す。
    /// <para>
    /// ウィンドウや仕切りを動かしているあいだ、<c>SizeChanged</c> は1ドラッグで
    /// 何十回も来る。そのたびに ViewModel へ渡すと時間軸を組み直すことになり、
    /// 掴んで動かすと画面が固まる（月ビューのマスと同じ理由）。
    /// </para>
    /// </summary>
    private readonly Settle _settle;

    private double _pendingViewportHeight;

    public WeekView()
    {
        InitializeComponent();
        _settle = new Settle(ApplyViewportHeight);
    }

    /// <summary>終日レーンの予定を2回押すと開く。</summary>
    private void OnAllDayEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditChipCommand.Execute(item as EventChipViewModel));

    /// <summary>終日レーンのタスクを2回押すと開く。1回押しはドラッグの始まりなので触らない。</summary>
    private void OnAllDayTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskChipCommand.Execute(item as TaskItem));

    private void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        // 1回押しはドラッグの始まり
        DragSession.Current.Press(sender, e);

        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not { } item) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        open(main, item);
        e.Handled = true;
    }

    /// <summary>
    /// 列の見出しを押すと、その日を選ぶ。
    /// <para>
    /// 2回押したら、その日に予定を足す（月・年ビューのマスと揃える）。日ビューへの
    /// 移動は右クリックメニューへ移した（<see cref="OnGoToDayClicked"/>）。
    /// </para>
    /// <para>
    /// 実働日計算パネルが開いているあいだは、1回押しをその「から」「まで」にも流す
    /// （月ビューのマスと同じ）。
    /// </para>
    /// </summary>
    private void OnDayHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WeekDayColumnViewModel day) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

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

    /// <summary>日付の行のラベルを2回押すと、その予定を開く。</summary>
    private void OnMilestoneClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not MilestoneViewModel milestone) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

    /// <summary>
    /// 時間軸に使える高さが変わったら、1時間の高さを決め直す。
    /// <para>
    /// 選んだ時間帯を縦いっぱいに割り付ける。高さは表示側にしか分からないので、
    /// ここで測って ViewModel へ渡す（月ビューのマスと同じ考え方）。
    /// </para>
    /// </summary>
    private void OnTimelineResized(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;

        _pendingViewportHeight = e.NewSize.Height;
        _settle.Poke();
    }

    private void ApplyViewportHeight()
    {
        if (DataContext is WeekViewModel week) week.ViewportHeight = _pendingViewportHeight;
    }

    // ------------------------------------------------------------------
    // ドラッグで動かす。掴み方と見せ方は DragSession が持つ
    // ------------------------------------------------------------------

    /// <summary>押したまま動かしたらドラッグを始める。</summary>
    private void OnAllDayDragging(object sender, MouseEventArgs e) =>
        DragSession.Current.DragIfMoved(sender, e);

    /// <summary>ドラッグ中、掴んでいるものをマウスに追わせる。</summary>
    private void OnDragMoving(object sender, DragEventArgs e) => DragSession.Current.Follow(e, this);

    /// <summary>落ちたら掴んでいるものを消す。どこに落ちてもここを通る。</summary>
    private void OnDragFinished(object sender, DragEventArgs e) => DragSession.Current.End();

    /// <summary>終日レーンの上を通っているあいだ。</summary>
    private void OnAllDayDragOver(object sender, DragEventArgs e) =>
        DragSession.ShowEffect(e, DayOf(sender) is not null && DragSession.Payload(e) is not null);

    /// <summary>
    /// 終日レーンに落とされた。
    /// <para>時刻を持っていた予定はここで終日になる。時間軸から出したのだから。</para>
    /// </summary>
    private void OnAllDayDropped(object sender, DragEventArgs e)
    {
        if (DayOf(sender) is not { } date) return;

        e.Handled = true;

        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        var copy = DragSession.IsCopy(e);

        switch (DragSession.Payload(e))
        {
            case EventChipViewModel chip:
                main.MoveEventToAllDay(chip.Id, date, copy);
                break;
            case TimeBlockViewModel block:
                main.MoveEventToAllDay(block.Id, date, copy);
                break;
            case MilestoneViewModel milestone:
                main.MoveEventTo(milestone.Id, date, copy);
                break;
            case TaskItem task:
                main.MoveTaskTo(task.Id, date, copy);
                break;
        }
    }

    /// <summary>落とし先の日。</summary>
    private static DateOnly? DayOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is WeekDayColumnViewModel column ? column.Date : null;

    // ------------------------------------------------------------------
    // ホイールで送る
    //
    // 月なら前後の月、週なら前後の週、日なら前後の日。指を止めずに見渡せる
    // ------------------------------------------------------------------

    /// <summary>ホイールを回したら前後へ送る。</summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        // Ctrl はビューの切り替えに使う。いちばん外が受けるので、ここでは何もしない
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        var command = e.Delta > 0 ? main.PreviousCommand : main.NextCommand;
        if (!command.CanExecute(null)) return;

        command.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 時間軸の上でホイールを回したとき。
    /// <para>
    /// 上下に余地があるうちはスクロールに譲る。1日ぶんが画面に収まっていて
    /// スクロールするものが無ければ、前後へ送る。
    /// </para>
    /// </summary>
    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer { ScrollableHeight: > 0 }) return;

        OnWheel(sender, e);
    }
}
