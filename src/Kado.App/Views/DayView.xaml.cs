using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Data.Repositories;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>日ビュー。表示だけを担い、状態は <c>DayViewModel</c> が持つ。</summary>
public partial class DayView : UserControl
{
    /// <summary>手が止まってから1時間の高さを決め直す（週ビューと同じ理由）。</summary>
    private readonly Settle _settle;

    private double _pendingViewportHeight;

    public DayView()
    {
        InitializeComponent();
        _settle = new Settle(ApplyViewportHeight);

        // 設定の変更で日ビューの実体そのものが作り直される。作りたての実体は既定の
        // 1時間の高さを持っていて、SizeChanged は来ない。実体を受け取ったところで、
        // いまの高さを測って渡す（月・年と同じ）
        DataContextChanged += (_, _) => MeasureAgain();
    }

    /// <summary>終日レーンの予定を2回押すと開く。</summary>
    private void OnAllDayEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditChipCommand.Execute(item as EventChipViewModel));

    /// <summary>終日レーンのタスクを2回押すと開く。1回押しはドラッグの始まりなので触らない。</summary>
    private void OnAllDayTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskChipCommand.Execute(item as ScheduledTask));

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

    /// <summary>いまの時間軸の高さを測り直して、すぐ ViewModel へ渡す。まだ大きさが無ければ何もしない。</summary>
    private void MeasureAgain()
    {
        if (Timeline.ActualHeight <= 0) return;

        _pendingViewportHeight = Timeline.ActualHeight;
        _settle.Now();
    }

    private void ApplyViewportHeight()
    {
        if (DataContext is DayViewModel day) day.ViewportHeight = _pendingViewportHeight;
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
            case ScheduledTask task:
                main.MoveTaskTo(task.Id, date, copy);
                break;
        }
    }

    /// <summary>落とし先の日。日ビューは1日ぶんしか出していない。</summary>
    private DateOnly? DayOf(object sender) =>
        DataContext is DayViewModel day ? day.Date : null;

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
