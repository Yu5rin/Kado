using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SlideinaCalendar.Data.Models;
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
    /// <summary>ドラッグが始まったところ。ここから少し動かすまでは掴んだと見なさない。</summary>
    private Point _dragOrigin;

    /// <summary>掴んでいるもの。押しただけでは動かさないので、ここに控えておく。</summary>
    private object? _dragging;

    public MonthView() => InitializeComponent();

    // ------------------------------------------------------------------
    // ドラッグで別の日へ移す
    //
    // 掴んで落とすのと、編集画面で日付を打ち直すのとでは手数が違う。
    // Ctrl を押しながらなら複製になる
    // ------------------------------------------------------------------

    /// <summary>押した場所を控える。実際に動かすかどうかは <see cref="OnChipDragging"/> で決める。</summary>
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;

        _dragOrigin = e.GetPosition(null);
        _dragging = (sender as FrameworkElement)?.DataContext;
    }

    /// <summary>
    /// 押したまま動かしたらドラッグを始める。
    /// <para>
    /// すぐに始めると、選ぼうとしただけの押し込みまで拾う。OS が決めている
    /// 最小の移動量を超えてからにする。
    /// </para>
    /// </summary>
    private void OnChipDragging(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging is null) return;

        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var moved = _dragging;
        _dragging = null;

        DragDrop.DoDragDrop(
            (DependencyObject)sender, moved, DragDropEffects.Move | DragDropEffects.Copy);
    }

    /// <summary>マスの上を通っているあいだ。落とせるかどうかをカーソルと面で示す。</summary>
    private void OnCellDragOver(object sender, DragEventArgs e)
    {
        var cell = (sender as FrameworkElement)?.DataContext as DayCellViewModel;
        var ok = cell is not null && Payload(e) is not null;

        e.Effects = ok ? EffectFor(e) : DragDropEffects.None;
        e.Handled = true;

        if (cell is not null) cell.IsDropTarget = ok;
    }

    /// <summary>マスから出たら印を消す。</summary>
    private void OnCellDragLeft(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DayCellViewModel cell) cell.IsDropTarget = false;
    }

    /// <summary>落とされたら、その日へ移す。Ctrl を押していれば複製する。</summary>
    private void OnCellDropped(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayCellViewModel cell) return;

        cell.IsDropTarget = false;
        e.Handled = true;

        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        var copy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);

        switch (Payload(e))
        {
            case EventChipViewModel chip:
                main.MoveEventTo(chip.Id, cell.Date, copy);
                break;
            case TaskItem task:
                main.MoveTaskTo(task.Id, cell.Date, copy);
                break;
        }
    }

    /// <summary>Ctrl を押していれば複製、押していなければ移動。</summary>
    private static DragDropEffects EffectFor(DragEventArgs e) =>
        e.KeyStates.HasFlag(DragDropKeyStates.ControlKey)
            ? DragDropEffects.Copy
            : DragDropEffects.Move;

    /// <summary>掴んでいるものを取り出す。予定でもタスクでもなければ null。</summary>
    private static object? Payload(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(EventChipViewModel))
            ? e.Data.GetData(typeof(EventChipViewModel))
            : e.Data.GetDataPresent(typeof(TaskItem))
                ? e.Data.GetData(typeof(TaskItem))
                : null;

    /// <summary>
    /// マスの高さが変わったら、並べる件数を決め直す。
    /// <para>
    /// 固定の件数だと、画面を広げてもマスの下が空いたまま「＋N」と出る。高さは
    /// 表示側にしか分からないので、ここで測って ViewModel へ渡す。
    /// </para>
    /// </summary>
    private void OnCellsResized(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;
        if (DataContext is not MonthViewModel month || month.Cells.Count == 0) return;

        var rows = month.Cells.Count / 7;
        if (rows <= 0) return;

        month.MaxChipsPerCell = DayCellViewModel.CapacityFor(e.NewSize.Height / rows);
    }

    /// <summary>1回押しでその日を選び、2回でその日に予定を足す。</summary>
    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayCellViewModel cell) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2) main.AddEventOnCommand.Execute(cell.Date);
        else main.SelectDateCommand.Execute(cell.Date);

        e.Handled = true;
    }

    /// <summary>
    /// マスに並ぶ予定を2回押すとその予定を開く。
    /// <para>
    /// ここで止めないとマス側の受け手に流れ、その日に新しい予定を足すことになる。
    /// 1回押しはマスと同じでその日を選ぶ。
    /// </para>
    /// </summary>
    private void OnEventChipClicked(object sender, MouseButtonEventArgs e) =>
        Handle(sender, e, (main, item) => main.EditChipCommand.Execute(item));

    /// <inheritdoc cref="OnEventChipClicked"/>
    private void OnTaskChipClicked(object sender, MouseButtonEventArgs e) =>
        Handle(sender, e, (main, item) => main.EditTaskChipCommand.Execute(item));

    private void Handle(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if ((sender as FrameworkElement)?.DataContext is not { } item) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        // 1回押しは日を選ぶと同時に、ドラッグの始まりでもある
        BeginDrag(sender, e);

        if (e.ClickCount == 2) open(main, item);
        else if (FindCell(sender as DependencyObject) is { } cell) main.SelectDateCommand.Execute(cell.Date);

        e.Handled = true;
    }

    /// <summary>その予定が乗っているマス。日を選ぶのに要る。</summary>
    private static DayCellViewModel? FindCell(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: DayCellViewModel cell }) return cell;
        }

        return null;
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
}
