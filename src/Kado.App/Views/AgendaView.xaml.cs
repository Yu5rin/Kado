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

    /// <summary>スクロールの本体。<c>ItemsControl</c> のテンプレートの中にあるので、適用後に取る。</summary>
    private ScrollViewer? _scroller;

    /// <summary>
    /// いま見ている位置の目印。いちばん上に見えている行の日付と、その行の上端が
    /// 画面の上端からどれだけ上にはみ出しているか。
    /// <para>
    /// 一覧はデータが変わるたびに <c>Rows</c> を丸ごと入れ替える。入れ物を作らずに済ませる仮想化では、
    /// 入れ替えるとスクロール位置が先頭に戻る（全行を作っていたときは、位置がそのまま残っていた）。
    /// 入れ替わったら、この目印の日へ戻す。
    /// </para>
    /// </summary>
    private DateOnly? _anchorDate;

    private double _anchorInset;

    /// <summary>位置合わせの最中。途中の位置（先頭へ戻った位置など）を目印に取らない。</summary>
    private bool _settling;

    /// <summary>最後に頼んだ位置合わせの通し番号。新しい依頼が来たら、古い依頼の続きは捨てる。</summary>
    private int _scrollSerial;

    /// <summary>
    /// 隠れているあいだに頼まれた位置合わせ。画面が出てきたときに行う。
    /// <para>隠れている（別のビューを出している）あいだは描き直されず、行の位置を測れない。</para>
    /// </summary>
    private (DateOnly Date, double Inset)? _alignWhenShown;

    /// <summary>位置を合わせ直す回数の上限。上端まで行けない行（末尾付近）で、いつまでも続けない。</summary>
    private const int MaxAlignAttempts = 3;

    public AgendaView()
    {
        InitializeComponent();

        // 全部出しているので、送るのは画面のほう。頼まれた日まで動かす
        DataContextChanged += (_, args) =>
        {
            if (_bound is not null)
            {
                _bound.ScrollRequested -= OnScrollRequested;
                _bound.PropertyChanged -= OnAgendaPropertyChanged;
            }

            _bound = args.NewValue as AgendaViewModel;

            // 作り直した一覧には、前の一覧の位置は引き継がない（先頭から始まる）。途中だった合わせ直しも捨てる
            _anchorDate = null;
            _alignWhenShown = null;
            _settling = false;
            _scrollSerial++;

            if (_bound is null) return;

            _bound.ScrollRequested += OnScrollRequested;
            _bound.PropertyChanged += OnAgendaPropertyChanged;

            // 日付をまたいで一覧が作り直されたときなど、新しい実体を受け取る。依頼はこの
            // 実体を作った直後に出ていて、まだ誰も聞いていなかった。控えてある位置合わせを
            // いま行う（作り直した一覧は先頭、何年も前から始まる）。
            // まだ表示される前なら、下の Loaded が行う
            if (IsLoaded) ScrollToPendingOrToday();
        };

        // 開いたときは、頼まれていた日（無ければ今日）のあたりを出す。先頭は何年も前かもしれない
        Loaded += (_, _) =>
        {
            HookScroller();
            ScrollToPendingOrToday();
        };

        // 隠れているあいだに頼まれていた位置合わせは、出てきたときに行う
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true || _alignWhenShown is not { } target || _bound is not { } agenda) return;

            _alignWhenShown = null;
            ScrollTo(agenda.RowOn(target.Date), target.Inset);
        };
    }

    /// <summary>控えてある位置合わせを行う。頼まれていなければ今日のあたり。</summary>
    private void ScrollToPendingOrToday()
    {
        if (_bound is not { } agenda) return;

        ScrollTo(agenda.TakePendingScroll() is { } date ? agenda.RowOn(date) : agenda.TodayRow);
    }

    private void OnScrollRequested(object? sender, DateOnly date)
    {
        if (_bound is { } agenda) ScrollTo(agenda.RowOn(date));
    }

    /// <summary>
    /// 一覧の行が丸ごと入れ替わった。見ていた位置へ戻す。
    /// <para>
    /// 位置の目印は、スクロールのたびに取ってある。入れ替わったあとに取り直しては遅い
    /// （先頭に戻った位置を取ってしまう）ので、ここから位置合わせが終わるまで取らない
    /// （<see cref="ScrollTo"/> が印を立てる）。
    /// </para>
    /// </summary>
    private void OnAgendaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgendaViewModel.Rows)) return;
        if (_anchorDate is not { } date || _bound is not { } agenda) return;

        ScrollTo(agenda.RowOn(date), _anchorInset);
    }

    private ScrollViewer? FindScroller()
    {
        if (_scroller is not null) return _scroller;

        // テンプレートはまだ適用されていないことがある
        Rows.ApplyTemplate();

        return _scroller = Rows.Template?.FindName("Scroller", Rows) as ScrollViewer;
    }

    private void HookScroller()
    {
        if (_scroller is null && FindScroller() is { } scroller) scroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>スクロールしたら、いま見ている位置を目印に控える。</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 位置合わせの途中や、入れ替わった直後の（先頭へ戻った）位置は取らない
        if (_settling) return;

        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;

        if (sender is ScrollViewer scroller) CaptureAnchor(scroller);
    }

    /// <summary>いちばん上に見えている行の日付と、その行がどれだけ上にはみ出しているかを控える。</summary>
    private void CaptureAnchor(ScrollViewer scroller)
    {
        if (FindPanel(scroller) is not { } panel) return;

        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement { DataContext: AgendaRowViewModel row } element) continue;

            var top = element.TransformToAncestor(scroller).Transform(default(Point)).Y;

            // 上端より上に、すっかり出てしまった行は飛ばす。上端にかかっている（または先頭の）行を目印にする
            if (top + element.ActualHeight <= 1) continue;

            _anchorDate = row.Date;
            _anchorInset = Math.Max(0, -top);
            return;
        }
    }

    /// <summary>行を並べているパネル（<c>VirtualizingStackPanel</c>）。</summary>
    private static VirtualizingPanel? FindPanel(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current is VirtualizingPanel panel) return panel;

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }

        return null;
    }

    /// <summary>
    /// その行を<b>いちばん上</b>に出す。
    /// <para>
    /// <c>BringIntoView</c> は見える位置まで最短で動かすので、下から来ると
    /// 画面のいちばん下に付いて止まる。探していた日が下端にあると、そこから先が
    /// 見えず、送った意味が薄い。上に置けば、その日から先が読める。
    /// </para>
    /// <para>
    /// <b>仮想化しているので、行の入れ物がまだ無いことがある。</b>先に <c>BringIndexIntoViewPublic</c> で
    /// その行の入れ物を作らせ（遠い行でも作られる）、その位置を測って上端へ動かす。
    /// 見えていない行の高さは見積りなので、動かしたあとにずれることがある。測り直して、
    /// 数回まで合わせ直す。
    /// </para>
    /// </summary>
    /// <param name="row">上端に出す行。</param>
    /// <param name="inset">行の上端を、画面の上端よりどれだけ上に置くか（位置を元へ戻すとき）。</param>
    private void ScrollTo(AgendaRowViewModel? row, double inset = 0)
    {
        // この依頼が最新。途中だった前の依頼の続きは捨てる
        var serial = ++_scrollSerial;

        if (row is null)
        {
            _settling = false;
            return;
        }

        _settling = true;

        // 隠れているあいだは測れない。出てきたときに行う
        if (!IsVisible)
        {
            _alignWhenShown = (row.Date, inset);
            return;
        }

        _alignWhenShown = null;
        Align(row, inset, serial, attempt: 0);
    }

    private void Align(AgendaRowViewModel row, double inset, int serial, int attempt)
    {
        // 描き終わる前に呼ばれると、行の入れ物がまだ無い。描き終わってから動かす
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 新しい依頼が来た。そちらが続きを受け持つ
            if (serial != _scrollSerial) return;

            var result = TryAlign(row, inset);

            if (result == AlignResult.Aligned || attempt >= MaxAlignAttempts - 1)
            {
                // 合った、または、これ以上は合わない（末尾付近など）。いまの位置を目印にする
                _settling = false;
                if (FindScroller() is { } scroller) CaptureAnchor(scroller);
                return;
            }

            Align(row, inset, serial, attempt + 1);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private enum AlignResult
    {
        /// <summary>まだ測れない（入れ物が無い・隠れている）。</summary>
        NotReady,

        /// <summary>動かした。合っているかは、測り直して確かめる。</summary>
        Moved,

        /// <summary>測ったら、すでに合っていた。</summary>
        Aligned,
    }

    /// <summary>1回合わせる。入れ物を作らせて、位置を測り、動かす。</summary>
    private AlignResult TryAlign(AgendaRowViewModel row, double inset)
    {
        if (_bound is not { } agenda || FindScroller() is not { } scroller) return AlignResult.NotReady;
        if (FindPanel(scroller) is not { } panel || !IsVisible) return AlignResult.NotReady;

        var index = IndexOf(agenda.Rows, row);
        if (index < 0) return AlignResult.NotReady;

        // 入れ物がまだ無い（見えていない）行は、作らせる。作ってある行は、そのまま測る
        // （作ってある行にもう一度頼むと、上にはみ出させて置いた位置を、全部見える位置へ戻してしまう）
        Rows.UpdateLayout();

        if (Rows.ItemContainerGenerator.ContainerFromIndex(index) is null)
        {
            panel.BringIndexIntoViewPublic(index);
            Rows.UpdateLayout();
        }

        if (Rows.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            return AlignResult.NotReady;
        }

        var top = container.TransformToAncestor(scroller).Transform(default(Point)).Y;
        var delta = top + inset;

        if (Math.Abs(delta) < 1) return AlignResult.Aligned;

        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + delta);
        scroller.UpdateLayout();
        return AlignResult.Moved;
    }

    private static int IndexOf(IReadOnlyList<AgendaRowViewModel> rows, AgendaRowViewModel row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row)) return i;
        }

        return -1;
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
        for (var at = from; at is not null; at = TreeWalk.ParentOf(at))
        {
            if (at is FrameworkElement { DataContext: AgendaRowViewModel row }) return row;
        }

        return null;
    }
}
