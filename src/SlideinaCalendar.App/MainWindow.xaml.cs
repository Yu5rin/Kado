using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App;

/// <summary>
/// ウィンドウモードの本体。状態は <see cref="MainViewModel"/> が持つ。
/// <para>
/// ここに書いてあるのはダブルクリックの受け口だけ。<c>InputBinding</c> は視覚ツリーに
/// 居ないため <c>RelativeSource</c> で祖先をたどれず、XAML だけでは繋げられない。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>現在時刻の線を動かす時計。1分ごとで足りる。</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>
    /// 左パネルの幅。閉じているあいだ列は 0 になるので、戻す幅をここに控える。
    /// </summary>
    private double _sideWidth = MainViewModel.DefaultSidePanelWidth;

    /// <summary>幅を入れ終わったか。<c>Loaded</c> は出し直すたびに来るので、一度だけにする。</summary>
    private bool _widthsRestored;

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => ViewModel?.UpdateNow(DateTime.Now);

        // 出した直後に一度合わせる。1分待たないと線が出ないのを避ける
        Loaded += (_, _) =>
        {
            ViewModel?.UpdateNow(DateTime.Now);
            _clock.Start();
            RestorePaneWidths();
        };

        Closed += (_, _) =>
        {
            _clock.Stop();
            SavePaneWidths();
        };
    }

    // ------------------------------------------------------------------
    // 3ペインの幅
    //
    // 両端はピクセルで、中央だけが「*」。こうしておけばウィンドウの幅が
    // 変わっても両端は動かず、増えたぶんは中央が受け取る
    // ------------------------------------------------------------------

    /// <summary>前回の幅を列に入れ、左パネルの開け閉めに追従させる。</summary>
    private void RestorePaneWidths()
    {
        if (_widthsRestored || ViewModel is not { } vm) return;

        _widthsRestored = true;

        _sideWidth = vm.SidePanelWidth;
        DetailColumn.Width = new GridLength(vm.DetailPaneWidth);
        ApplySidePanel(vm.IsSidePanelOpen);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsSidePanelOpen))
            {
                ApplySidePanel(vm.IsSidePanelOpen);
            }
        };
    }

    /// <summary>
    /// 左パネルの開け閉め。
    /// <para>
    /// 閉じるときは列ごと畳む。中身を隠すだけでは、手で決めた幅ぶんの余白が残る。
    /// 開くときは畳む前の幅に戻す。
    /// </para>
    /// </summary>
    private void ApplySidePanel(bool isOpen)
    {
        if (isOpen)
        {
            SideColumn.MinWidth = MainViewModel.MinSidePanelWidth;
            SideColumn.Width = new GridLength(_sideWidth);
            return;
        }

        if (SideColumn.ActualWidth > 0) _sideWidth = SideColumn.ActualWidth;

        // 下限を外さないと 0 まで畳めない
        SideColumn.MinWidth = 0;
        SideColumn.Width = new GridLength(0);
    }

    /// <summary>手で決めた幅を覚える。次に起動したときも同じ幅で出す。</summary>
    private void SavePaneWidths()
    {
        if (ViewModel is not { } vm) return;

        vm.SidePanelWidth = SideColumn.ActualWidth > 0 ? SideColumn.ActualWidth : _sideWidth;
        vm.DetailPaneWidth = DetailColumn.ActualWidth;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>検索の結果を押したら、その日へ移って開く。</summary>
    private void OnSearchResultClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SearchResultViewModel found) return;

        ViewModel?.OpenSearchResult(found);
        e.Handled = true;
    }

    /// <summary>Esc で検索をやめる。</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        ViewModel?.ClearSearch();
        e.Handled = true;
    }

    /// <summary>右ペインの予定。ダブルクリックで編集画面を開く。</summary>
    private void OnEventRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DataContextOf<DayEventViewModel>(sender) is not { } target) return;

        ViewModel?.EditEventCommand.Execute(target);
        e.Handled = true;
    }

    /// <summary>右ペインのタスク。ダブルクリックで編集画面を開く。</summary>
    private void OnTaskRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DataContextOf<TaskListItemViewModel>(sender) is not { } target) return;

        ViewModel?.EditTaskCommand.Execute(target);
        e.Handled = true;
    }

    /// <summary>設定ボタン。押した位置にメニューを開く。</summary>
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;

        // 既定の右クリック待ちではなく、左クリックで開く
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    /// <summary>日付の行のラベルを2回押すと、その予定を開く。</summary>
    private void OnMilestoneClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (DataContextOf<MilestoneViewModel>(sender) is not { } milestone) return;

        ViewModel?.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // 左パネルの並べ替え
    //
    // 掴めるのは行の右端の取っ手だけ。行のどこでも掴めるようにすると、
    // チェックを入り切りするふつうの押し下げと区別が付かない
    // ------------------------------------------------------------------

    private Point _dragStart;
    private SourceListItemViewModel? _dragging;

    private void OnSourceRowPressed(object sender, MouseButtonEventArgs e)
    {
        if (!IsGrip(e.OriginalSource as DependencyObject)) return;
        if (DataContextOf<SourceListItemViewModel>(sender) is not { } item) return;

        _dragging = item;
        _dragStart = e.GetPosition(null);

        // 取っ手ではチェックを切り替えない
        e.Handled = true;
    }

    private void OnSourceRowMoved(object sender, MouseEventArgs e)
    {
        if (_dragging is not { } moved) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = null;
            return;
        }

        // 少し動かすまでは始めない。押しただけで掴んだことにすると、
        // 取っ手を軽く触っただけで並びが変わる
        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragging = null;
        DragDrop.DoDragDrop((DependencyObject)sender, moved, DragDropEffects.Move);
    }

    private void OnSourceRowDragOver(object sender, DragEventArgs e)
    {
        var ok = CanDrop(sender, e);

        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        // どこへ入るのかを線で示す。示さないと、落としてみるまで分からない
        ViewModel?.ShowDropHint(
            ok ? DataContextOf<SourceListItemViewModel>(sender) : null, IsUpperHalf(sender, e));
    }

    private void OnSourceRowDragLeave(object sender, DragEventArgs e) =>
        ViewModel?.ShowDropHint(null, above: false);

    private void OnSourceRowDropped(object sender, DragEventArgs e)
    {
        if (!CanDrop(sender, e)) return;

        ViewModel?.MoveSource(
            Dragged(e), DataContextOf<SourceListItemViewModel>(sender), IsUpperHalf(sender, e));

        e.Handled = true;
    }

    /// <summary>行の上半分にいるか。上半分ならその行の上、下半分なら下に入る。</summary>
    private static bool IsUpperHalf(object sender, DragEventArgs e) =>
        sender is not FrameworkElement row ||
        e.GetPosition(row).Y < row.ActualHeight / 2;

    /// <summary>落とせる先か。カレンダーとタスクリストの間では動かさない。</summary>
    private bool CanDrop(object sender, DragEventArgs e) =>
        Dragged(e) is { } moved &&
        DataContextOf<SourceListItemViewModel>(sender) is { } target &&
        !ReferenceEquals(moved, target) &&
        ViewModel?.CanMoveSource(moved, target) == true;

    private static SourceListItemViewModel? Dragged(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(SourceListItemViewModel))
            ? e.Data.GetData(typeof(SourceListItemViewModel)) as SourceListItemViewModel
            : null;

    /// <summary>押した場所が並べ替えの取っ手の中か。</summary>
    private static bool IsGrip(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "Grip" }) return true;
        }

        return false;
    }

    private static T? DataContextOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;
}
