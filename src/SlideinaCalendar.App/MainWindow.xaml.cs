using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SlideinaCalendar.Presentation.Settings;
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

    /// <summary>
    /// いまの置き場所。
    /// <para>
    /// 閉じるときに読むのではなく、動かすたびにここへ控える。閉じたあとでは
    /// <c>RestoreBounds</c> が当てにならず、更新のための終了（<c>Shutdown</c>）では
    /// <c>Closing</c> も来ない。動いた時点で控えておけば、どちらの終わり方でも残る。
    /// </para>
    /// </summary>
    private WindowPlacement _placement = WindowPlacement.Unknown;

    /// <summary>置き場所の出し入れ。渡されなければ覚えない。</summary>
    public WindowPlacementStore? Placements { get; init; }

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => ViewModel?.UpdateNow(DateTime.Now);

        // 窓ができた時点で入れる。出してから動かすと、一度出てから飛ぶのが見える
        SourceInitialized += (_, _) => RestorePlacement();

        LocationChanged += (_, _) => TrackPlacement();
        SizeChanged += (_, _) =>
        {
            TrackPlacement();
            PublishLayoutWidth();
        };
        StateChanged += (_, _) => TrackPlacement();

        // 出した直後に一度合わせる。1分待たないと線が出ないのを避ける
        Loaded += (_, _) =>
        {
            ViewModel?.UpdateNow(DateTime.Now);
            _clock.Start();
            RestorePaneWidths();
            PublishLayoutWidth();
        };

        Closed += (_, _) =>
        {
            _clock.Stop();
            SavePaneWidths();
            Placements?.Save(_placement);
        };
    }

    // ------------------------------------------------------------------
    // ウィンドウの置き場所と大きさ
    // ------------------------------------------------------------------

    /// <summary>前回の置き場所で出す。最大化で終わっていれば最大化で出す。</summary>
    private void RestorePlacement()
    {
        if (Placements is not { } store) return;

        // 前に使っていた画面が無くなっていることがある。外付けのディスプレイを
        // 外したまま起動すると、画面の外に開いて手が出せなくなる
        var saved = store.Load().ClampTo(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        Width = saved.Width;
        Height = saved.Height;

        if (saved.HasPosition)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
        }

        _placement = saved;

        // 大きさを入れたあとで最大化する。先に最大化すると、元に戻したときの
        // 大きさが既定のまま残る
        if (saved.IsMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>いまの置き場所を控える。</summary>
    private void TrackPlacement()
    {
        switch (WindowState)
        {
            // 最小化で終わった次の起動でアイコンのまま出てくると、
            // 立ち上がったのかどうか分からない。覚えない
            case WindowState.Minimized:
                return;

            // 最大化中の大きさは覚えない。覚えるのは元に戻したときの大きさのほう
            case WindowState.Maximized:
                _placement = _placement with { IsMaximized = true };
                return;

            default:
                _placement = new WindowPlacement(Left, Top, Width, Height, IsMaximized: false);
                return;
        }
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
        _detailWidth = vm.DetailPaneWidth;
        DetailColumn.Width = new GridLength(vm.DetailPaneWidth);
        ApplySidePanel(vm.IsSidePanelOpen);

        ApplyPanes(vm);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsSidePanelOpen))
            {
                ApplySidePanel(vm.IsSidePanelOpen);

                // 誰が残りを受け取るかが変わる。開けたあとに呼ぶ
                ApplyPanes(vm);
            }
            else if (args.PropertyName is nameof(MainViewModel.IsMainViewOpen)
                     or nameof(MainViewModel.IsDetailPaneOpen))
            {
                ApplyPanes(vm);
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

    /// <summary>
    /// 中央と右ペインの出し分け。
    /// <para>
    /// 畳むときは列ごと 0 にする。中身を隠すだけでは、手で決めた幅ぶんの余白が残る。
    /// 中央を畳んだときは、右ペインが伸びて残りを受け取る。
    /// </para>
    /// </summary>
    private void ApplyPanes(MainViewModel vm)
    {
        if (vm.IsDetailPaneOpen && DetailColumn.ActualWidth > 0) _detailWidth = DetailColumn.ActualWidth;

        // 中央を畳んだら、右ペインが「*」になって残りを埋める。
        // ピクセルのままだと、窓を広げたぶんが誰にも行き渡らない
        DetailColumn.Width = vm switch
        {
            { IsDetailPaneOpen: false } => new GridLength(0),
            { IsMainViewOpen: false } => new GridLength(1, GridUnitType.Star),
            _ => new GridLength(_detailWidth),
        };

        DetailColumn.MinWidth = vm.IsDetailPaneOpen ? MainViewModel.MinDetailPaneWidth : 0;

        // 上限も外す。「*」にしても上限（既定 640px）で止まるので、中央を畳んだのに
        // 右ペインがそこまでしか伸びず、その先が黒いまま余った
        DetailColumn.MaxWidth = vm.IsMainViewOpen ? MainViewModel.MaxDetailPaneWidth
            : double.PositiveInfinity;

        // 中身を隠すだけでは列が残る。「*」の列は、中が畳まれていても場所を取り続ける。
        // 実機で「中央を消しても中央のエリアが残る」となったのはこれ
        // 中央の下げ止まり。帯として使うときはここまで詰める
        MainColumn.MinWidth = vm.IsMainViewOpen ? MainViewModel.MinMainViewWidth : 0;
        MainColumn.Width = vm.IsMainViewOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // 左パネルだけ残したときも同じ。上限のまま置くと、右側が黒く余る
        if (vm.IsSidePanelOpen)
        {
            var sideOnly = vm is { IsMainViewOpen: false, IsDetailPaneOpen: false };

            SideColumn.MaxWidth = sideOnly ? double.PositiveInfinity
                : MainViewModel.MaxSidePanelWidth;
            SideColumn.Width = sideOnly ? new GridLength(1, GridUnitType.Star)
                : new GridLength(_sideWidth);
        }

        // 掴みしろだけ残っても、つまんで動かす相手がいない
        DetailSplitter.Visibility = vm is { IsMainViewOpen: true, IsDetailPaneOpen: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>右ペインを畳むあいだ、戻す幅をここに控える。</summary>
    private double _detailWidth = MainViewModel.DefaultDetailPaneWidth;

    /// <summary>手で決めた幅を覚える。次に起動したときも同じ幅で出す。</summary>
    private void SavePaneWidths()
    {
        if (ViewModel is not { } vm) return;

        vm.SidePanelWidth = SideColumn.ActualWidth > 0 ? SideColumn.ActualWidth : _sideWidth;
        vm.DetailPaneWidth = vm.IsDetailPaneOpen && DetailColumn.ActualWidth > 0
            ? DetailColumn.ActualWidth
            : _detailWidth;
    }

    // ------------------------------------------------------------------
    // ドック幅のグリップ（要件書 7.2）
    //
    // 端に寄せているあいだは枠を消しているので、OS の掴みしろが無い。
    // 画面の内側にあたる辺に自前の掴みしろを置き、ここで幅を変える。
    // ------------------------------------------------------------------

    /// <summary>掴んだときの画面上の位置。ウィンドウ内の座標だと、動かすたびにずれる。</summary>
    private Point? _gripFrom;

    /// <summary>掴んだときの幅。</summary>
    private double _gripWidth;

    private void OnGripPressed(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        _gripFrom = PointToScreen(e.GetPosition(this));
        _gripWidth = vm.Shell.DockWidth;

        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnGripDragging(object sender, MouseEventArgs e)
    {
        if (_gripFrom is not { } from || ViewModel is not { } vm) return;

        var moved = PointToScreen(e.GetPosition(this)).X - from.X;

        // 右に寄せていれば、左へ引くほど広くなる。左に寄せていれば逆
        vm.Shell.DockWidth = vm.Shell.IsAtLeft ? _gripWidth + moved : _gripWidth - moved;
    }

    private void OnGripReleased(object sender, MouseButtonEventArgs e)
    {
        if (_gripFrom is null) return;

        _gripFrom = null;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// Ctrl＋ホイールでビューを切り替える。
    /// <para>
    /// 一覧 → 年 → 月 → 週 → 日 の並びを1つずつ動く。回すたびに見ている範囲が
    /// 狭まる（または広がる）。選んでいる日はそのまま持っていくので、切り替えた
    /// 先でも同じ日を見ている。
    /// </para>
    /// <para>
    /// どのビューの上でも効かせたいので、いちばん外で受ける。中のビューは Ctrl を
    /// 押しているあいだホイールを受けない作りにしてある。
    /// </para>
    /// </summary>
    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (ViewModel is not { } vm) return;

        // 手前に回すと細かく、奥に回すと粗く。地図と同じ向き
        if (e.Delta < 0) vm.ZoomOutCommand.Execute(null);
        else vm.ZoomInCommand.Execute(null);

        e.Handled = true;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// 中身がもらっている幅を渡す。詰め方はこれで決まる。
    /// <para>
    /// <b><c>Window.ActualWidth</c> を使わない。</b>あれは見えないリサイズ枠
    /// （左右7〜8px）を含むうえ、<c>MinWidth</c> を下回らない。中身の根元の幅なら
    /// どちらのずれも無い。
    /// </para>
    /// <para>
    /// 流すのはここ1か所だけにする。詰め方は ViewModel が <c>LayoutWidth</c> の
    /// 変化を受けて決めるので、こちらから重ねて頼まない。
    /// </para>
    /// </summary>
    private void PublishLayoutWidth()
    {
        if (ViewModel is not { } vm) return;

        vm.Shell.LayoutWidth = Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;
    }

    /// <summary>検索の結果を押したら、その日へ移って開く。</summary>
    private void OnSearchResultClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SearchResultViewModel found) return;

        ViewModel?.OpenSearchResult(found);
        e.Handled = true;
    }

    /// <summary>クイック入力は Enter で入れる。</summary>
    private void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } vm || !vm.QuickCommand.CanExecute(null)) return;

        vm.QuickCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 畳んであるときの虫めがね。
    /// <para>開いたら打ち始められるよう、入力欄に手を渡す。</para>
    /// </summary>
    private void OnCompactSearchClicked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }, System.Windows.Threading.DispatcherPriority.Input);

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
