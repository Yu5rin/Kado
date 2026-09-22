using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>
/// 一覧ビュー。
/// <para>
/// 持っているぶんを全部出すので、送るのではなくスクロールで動く。行を押すと
/// その日を選ぶ。予定・タスクはダブルクリックで編集、右クリックでメニュー。
/// </para>
/// </summary>
public partial class AgendaView : UserControl
{
    private AgendaViewModel? _bound;

    public AgendaView()
    {
        InitializeComponent();

        // 全部出しているので、送るのは画面のほう。頼まれた日まで動かす
        DataContextChanged += (_, args) =>
        {
            if (_bound is not null) _bound.ScrollRequested -= OnScrollRequested;

            _bound = args.NewValue as AgendaViewModel;

            if (_bound is not null) _bound.ScrollRequested += OnScrollRequested;
        };

        // 開いたときは今日のあたりを出す。先頭は何年も前かもしれない
        Loaded += (_, _) =>
        {
            if (_bound is { } agenda) ScrollTo(agenda.TodayRow);
        };
    }

    private void OnScrollRequested(object? sender, DateOnly date)
    {
        if (_bound is { } agenda) ScrollTo(agenda.RowOn(date));
    }

    /// <summary>
    /// その行を<b>いちばん上</b>に出す。
    /// <para>
    /// <c>BringIntoView</c> は見える位置まで最短で動かすので、下から来ると
    /// 画面のいちばん下に付いて止まる。探していた日が下端にあると、そこから先が
    /// 見えず、送った意味が薄い。上に置けば、その日から先が読める。
    /// </para>
    /// </summary>
    private void ScrollTo(AgendaRowViewModel? row)
    {
        if (row is null) return;

        // 描き終わる前に呼ばれると、行の入れ物がまだ無い
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Rows.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container) return;
            if (Scroller is not { } scroller) return;

            var top = container.TransformToAncestor(scroller).Transform(default(Point)).Y;

            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + top);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>行を押したらその日を選ぶ。月ビューのマスと同じ。</summary>
    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgendaRowViewModel row) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2) main.AddEventOnCommand.Execute(row.Date);
        else main.SelectDateCommand.Execute(row.Date);

        e.Handled = true;
    }

    private void OnEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditEventCommand.Execute(item));

    private void OnTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskCommand.Execute(item));

    /// <summary>
    /// 予定やタスクを押したとき。
    /// <para>
    /// ここで止めないと行側の受け手に流れ、その日に新しい予定を足すことになる。
    /// 1回押しは行と同じでその日を選ぶ。
    /// </para>
    /// </summary>
    private static void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if (sender is not FrameworkElement element || element.DataContext is not { } item) return;
        if (Window.GetWindow(element)?.DataContext is not MainViewModel main) return;

        if (e.ClickCount == 2) open(main, item);
        else if (Row(element) is { } row) main.SelectDateCommand.Execute(row.Date);

        e.Handled = true;
    }

    /// <summary>その中身が載っている行。日を選ぶのに要る。</summary>
    private static AgendaRowViewModel? Row(DependencyObject from)
    {
        for (var at = from; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { DataContext: AgendaRowViewModel row }) return row;
        }

        return null;
    }
}
