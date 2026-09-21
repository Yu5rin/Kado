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
    /// <summary>
    /// 手が止まってから組み直す。
    /// <para>
    /// マスの数を決め直すと 42 マスを作り直すことになる。ドラッグのあいだ毎回
    /// やると掴んだ瞬間に固まる。週と日が軽いのは、大きさに合わせて組み直す
    /// ものを持たないから。
    /// </para>
    /// </summary>
    private readonly Settle _settle;

    public MonthView()
    {
        InitializeComponent();

        _settle = new Settle(FitCells);

        // 詰めた形に切り替わったら、マスの高さを決め直す
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is MonthViewModel before) before.PropertyChanged -= OnMonthChanged;
            if (args.NewValue is MonthViewModel after) after.PropertyChanged += OnMonthChanged;

            _settle.Now();
        };
    }

    private void OnMonthChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonthViewModel.IsCompact)) _settle.Now();
    }

    /// <summary>
    /// マスの高さが変わったら、並べる件数を決め直す。
    /// <para>
    /// 固定の件数だと、画面を広げてもマスの下が空いたまま「＋N」と出る。高さは
    /// 表示側にしか分からないので、ここで測って ViewModel へ渡す。
    /// </para>
    /// </summary>
    private void OnCellsResized(object sender, SizeChangedEventArgs e) => _settle.Poke();

    /// <summary>
    /// マスの大きさを決める。
    /// <para>
    /// ふつうの形では、高さから並べられる件数を割り出して ViewModel へ渡す。
    /// 詰めた形（スリムパネル）では、<b>マスを正方形に近づける</b>。ひと月の
    /// 並びを追うのがこの形での役目で、縦に伸ばしても読めるものは増えない。
    /// </para>
    /// </summary>
    private void FitCells()
    {
        if (DataContext is not MonthViewModel month || month.Cells.Count == 0) return;

        var rows = month.Cells.Count / 7;
        if (rows <= 0) return;

        // マス幅が64pxを切ったら詰めた形にする（項目6）。同じテンプレートを
        // 使い回すので描画側の変更は要らない。スリムパネルの月はいつもこの幅を
        // 下回るので、既存の「常に詰める」指定（MainViewModel.BuildViews）と
        // 衝突しない
        if (CellsHost.ActualWidth > 0) month.IsCompact = CellsHost.ActualWidth / 7 < 64;

        if (month.IsCompact)
        {
            // 正方形は「これ以上は潰さない」の線。高さに余裕があれば伸びる。
            // 決め打ちにしていたら、仕切りを下げてもカレンダーが伸びなかった
            CellsHost.MinHeight = Math.Round(CellsHost.ActualWidth / 7) * rows;
            return;
        }

        CellsHost.MinHeight = 0;

        if (CellsHost.ActualHeight <= 0) return;

        month.MaxChipsPerCell = DayCellViewModel.CapacityFor(CellsHost.ActualHeight / rows);
    }

    // ------------------------------------------------------------------
    // ドラッグで別の日へ移す
    //
    // 掴んで落とすのと、編集画面で日付を打ち直すのとでは手数が違う。
    // Ctrl を押しながらなら複製。掴み方と見せ方は DragSession が持つ
    // ------------------------------------------------------------------

    /// <summary>押したまま動かしたらドラッグを始める。</summary>
    private void OnChipDragging(object sender, MouseEventArgs e) =>
        DragSession.Current.DragIfMoved(sender, e);

    /// <summary>ドラッグ中、掴んでいるものをマウスに追わせる。</summary>
    private void OnDragMoving(object sender, DragEventArgs e) =>
        DragSession.Current.Follow(e, this);

    /// <summary>落ちたら掴んでいるものを消す。マスの外に落ちたときもここを通る。</summary>
    private void OnDragFinished(object sender, DragEventArgs e) => DragSession.Current.End();

    /// <summary>マスの上を通っているあいだ。落とせるかどうかをカーソルと面で示す。</summary>
    private void OnCellDragOver(object sender, DragEventArgs e)
    {
        var cell = (sender as FrameworkElement)?.DataContext as DayCellViewModel;
        var ok = cell is not null && DragSession.Payload(e) is not null;

        DragSession.ShowEffect(e, ok);

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

        var copy = DragSession.IsCopy(e);

        switch (DragSession.Payload(e))
        {
            case EventChipViewModel chip:
                main.MoveEventTo(chip.Id, cell.Date, copy);
                break;
            case MilestoneViewModel milestone:
                main.MoveEventTo(milestone.Id, cell.Date, copy);
                break;

            // 週ビューの時間軸から持ってきたもの。月ビューは時刻を持たないので、
            // 日だけを変えて時刻はそのままにする
            case TimeBlockViewModel block:
                main.MoveEventTo(block.IsWorkBlock ? null : block.Id, cell.Date, copy);
                break;
            case TaskItem task:
                main.MoveTaskTo(task.Id, cell.Date, copy);
                break;
        }
    }

    /// <summary>
    /// 1回押しでその日を選び、2回でその日に予定を足す。
    /// <para>
    /// 実働日計算パネルが開いているあいだは、1回押しをその「から」「まで」にも
    /// 流す（要件書 4.5）。Shift を押していれば「まで」、押していなければ「から」。
    /// 日を選ぶ今までどおりの動きは変えない。
    /// </para>
    /// </summary>
    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DayCellViewModel cell) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2)
        {
            main.AddEventOnCommand.Execute(cell.Date);
        }
        else
        {
            main.SelectDateCommand.Execute(cell.Date);

            if (main.IsWorkdayCalculatorOpen)
            {
                main.FeedWorkdayCalculator(cell.Date, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
        }

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
        DragSession.Current.Press(sender, e);

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
        if ((sender as FrameworkElement)?.DataContext is not MilestoneViewModel milestone) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        // 1回押しはドラッグの始まり。仕様期限なども別の日へ動かせる
        DragSession.Current.Press(sender, e);

        if (e.ClickCount != 2) return;

        main.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

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
}
