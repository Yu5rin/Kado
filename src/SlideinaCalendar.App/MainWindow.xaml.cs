using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SlideinaCalendar.App.Shell;
using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App;

/// <summary>
/// ウィンドウモードの本体。状態は <see cref="MainViewModel"/> が持つ。
/// <para>
/// ここに書いてあるのはダブルクリックの受け口だけ。<c>InputBinding</c> は視覚ツリーに
/// 居ないため <c>RelativeSource</c> で祖先をたどれず、XAML だけでは繋げられない。
/// </para>
/// <para>
/// <see cref="ISlideRevealHost"/> も実装する。<c>ShellController</c> は <c>Window</c>
/// 型で窓を受け取っているので、開く演出（滑り出し）のあいだ中身（<c>Root</c>）を
/// 固定する役目はこちらに持たせ、そちら越しに頼んでもらう。
/// </para>
/// </summary>
public partial class MainWindow : Window, ISlideRevealHost
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

    /// <summary>
    /// いちばん細くできる幅を設定から受ける。
    /// <para>
    /// <b>ここが窓の下限をそのまま決める。</b>WPF は <c>MinWidth</c> を Windows へ
    /// 「これ以上小さくできない」として答えるので、他に仕掛けは要らない。
    /// </para>
    /// </summary>
    public AppSettings? Settings
    {
        get => _settings;
        init
        {
            _settings = value;

            if (value is null) return;

            MinWidth = value.MinWidth;
            value.Changed += (_, _) => MinWidth = value.MinWidth;
        }
    }

    private readonly AppSettings? _settings;

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += (_, _) => ViewModel?.UpdateNow(DateTime.Now);

        // 窓ができた時点で入れる。出してから動かすと、一度出てから飛ぶのが見える
        SourceInitialized += (_, _) => RestorePlacement();

        // 幅を伝えるとツールバーの詰め方が決まり、レイアウトが走る。ドラッグの
        // あいだ毎回やると重いので、手が止まってから1回だけにする
        _settle = new Views.Settle(() =>
        {
            TrackPlacement();
            PublishLayoutWidth();
        });

        // 置き場所とパネル幅の DB 書き込み（項目7）。強制終了・電源断だと Closed が
        // 来ず、最後に正常終了したときの位置に戻ってしまう。落ち着いたところでも
        // 書いておく。DB を叩くので、_settle よりゆったりした間隔にする
        _persist = new Views.Settle(SavePlacementAndPanes, TimeSpan.FromSeconds(3));

        LocationChanged += (_, _) =>
        {
            TrackPlacement();
            _persist.Poke();
        };

        SizeChanged += (_, _) =>
        {
            _settle.Poke();
            _persist.Poke();
        };
        StateChanged += (_, _) =>
        {
            TrackPlacement();
            _persist.Poke();
        };

        // ステータス行（項目1）。出すたびに数秒後へ仕切り直し、最後の1件だけを消す
        _statusClear = new Views.Settle(() => ViewModel?.ClearStatusMessage(), TimeSpan.FromSeconds(4.5));

        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is MainViewModel before)
            {
                before.PropertyChanged -= OnStatusMessageChanged;
                before.PropertyChanged -= OnBusyChanged;
            }
            if (args.NewValue is MainViewModel after)
            {
                after.PropertyChanged += OnStatusMessageChanged;
                after.PropertyChanged += OnBusyChanged;
            }
        };

        // 出した直後に一度合わせる。1分待たないと線が出ないのを避ける
        Loaded += (_, _) =>
        {
            ViewModel?.UpdateNow(DateTime.Now);
            _clock.Start();
            RestorePaneWidths();
            _settle.Now();
        };

        Closed += (_, _) =>
        {
            _clock.Stop();
            _statusClear.Dispose();
            _persist.Dispose();
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

        ApplyPanes(vm);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsSidePanelOpen)
                or nameof(MainViewModel.IsMainViewOpen)
                or nameof(MainViewModel.IsDetailPaneOpen)
                or nameof(MainViewModel.IsSlimPanelOpen))
            {
                ApplyPanes(vm);
            }
        };
    }

    /// <summary>残りの幅を受け取るのは、どのパネルか。</summary>
    private enum Filler
    {
        Main,
        Detail,
        Side,
        Slim,
    }

    /// <summary>
    /// パネルの出し分けと幅。
    /// <para>
    /// <b>残りを受け取るのは1つだけ。</b>中央 → 右 → 左 → スリム の順で決める。
    /// どれも「*」でないと、窓を広げたぶんが誰にも行き渡らず、黒いまま余る。
    /// パネルごとに別々の条件で決めていたら、スリムパネルだけを出したときに
    /// 受け取り手がいなくなっていた。
    /// </para>
    /// <para>
    /// 畳むときは列ごと 0 にする。中身を隠すだけでは、手で決めた幅ぶんの余白が残る。
    /// </para>
    /// </summary>
    private void ApplyPanes(MainViewModel vm)
    {
        // 手で決めた幅を控える。畳んで開き直したとき、そこへ戻す
        if (vm.IsSlimPanelOpen && SlimColumn.ActualWidth > 0) _slimWidth = SlimColumn.ActualWidth;
        if (vm.IsSidePanelOpen && SideColumn.ActualWidth > 0) _sideWidth = SideColumn.ActualWidth;
        if (vm.IsDetailPaneOpen && DetailColumn.ActualWidth > 0) _detailWidth = DetailColumn.ActualWidth;

        var filler =
            vm.IsMainViewOpen ? Filler.Main
            : vm.IsDetailPaneOpen ? Filler.Detail
            : vm.IsSidePanelOpen ? Filler.Side
            : Filler.Slim;

        Fit(SlimColumn, vm.IsSlimPanelOpen, filler == Filler.Slim, _slimWidth,
            MainViewModel.MinSlimPanelWidth, MainViewModel.MaxSlimPanelWidth);

        Fit(SideColumn, vm.IsSidePanelOpen, filler == Filler.Side, _sideWidth,
            MainViewModel.MinSidePanelWidth, MainViewModel.MaxSidePanelWidth);

        Fit(MainColumn, vm.IsMainViewOpen, filler == Filler.Main, MainViewModel.MinMainViewWidth,
            MainViewModel.MinMainViewWidth, double.PositiveInfinity);

        Fit(DetailColumn, vm.IsDetailPaneOpen, filler == Filler.Detail, _detailWidth,
            MainViewModel.MinDetailPaneWidth, MainViewModel.MaxDetailPaneWidth);

        // 掴みしろは、つまんで動かす相手がいるときだけ出す
        SlimSplitter.Visibility = Between(
            vm.IsSlimPanelOpen, vm.IsSidePanelOpen || vm.IsMainViewOpen || vm.IsDetailPaneOpen);
        SideSplitter.Visibility = Between(
            vm.IsSidePanelOpen, vm.IsMainViewOpen || vm.IsDetailPaneOpen);
        DetailSplitter.Visibility = Between(vm.IsDetailPaneOpen, vm.IsMainViewOpen);
    }

    /// <summary>列を、開いているか・残りを受け取るかに合わせて整える。</summary>
    private static void Fit(
        ColumnDefinition column, bool open, bool fills, double width, double min, double max)
    {
        column.MinWidth = open ? min : 0;

        // 受け取り手のときは上限を外す。付けたままだと、そこで止まって先が余る
        column.MaxWidth = open && !fills ? max : double.PositiveInfinity;

        column.Width = (open, fills) switch
        {
            (false, _) => new GridLength(0),
            (true, true) => new GridLength(1, GridUnitType.Star),
            _ => new GridLength(width),
        };
    }

    private static Visibility Between(bool left, bool right) =>
        left && right ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>右ペインを畳むあいだ、戻す幅をここに控える。</summary>
    private double _detailWidth = MainViewModel.DefaultDetailPaneWidth;

    /// <summary>スリムパネルを畳むあいだ、戻す幅をここに控える。</summary>
    private double _slimWidth = MainViewModel.DefaultSlimPanelWidth;

    /// <summary>大きさが落ち着いてから、幅を伝える。</summary>
    private readonly Views.Settle _settle;

    /// <summary>ステータス行（項目1）を数秒後に消すためのタイマー。</summary>
    private readonly Views.Settle _statusClear;

    /// <summary>
    /// 置き場所とパネル幅を落ち着いてから DB へ書く（項目7）。
    /// <para>
    /// <c>Closed</c> だけだと、強制終了・電源断で前回正常終了したときの位置に
    /// 戻ってしまう。書き込みのたびに DB を叩くので、間隔は控えめに取ってある。
    /// </para>
    /// </summary>
    private readonly Views.Settle _persist;

    /// <summary>StatusMessage が変わるたびに、消すまでの時間を仕切り直す。</summary>
    private void OnStatusMessageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.StatusMessage)) return;

        // 空になった（もう消えている）ときは、あらためて仕切り直さない
        if (ViewModel?.StatusMessage is null) return;

        _statusClear.Poke();
    }

    /// <summary>
    /// 取り込み・復元の間、待機カーソルに変える（項目5）。
    /// <para>
    /// 重い処理は UI スレッドのまま動くので、<c>StatusMessage</c> の「実行しています…」は
    /// 描画のタイミングによっては間に合わないことがある。カーソルは OS へ直接効くので、
    /// この形でも確実に見える。
    /// </para>
    /// </summary>
    private void OnBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsBusy)) return;

        Mouse.OverrideCursor = ViewModel?.IsBusy == true ? Cursors.Wait : null;
    }

    /// <summary>
    /// 落ち着いたところで、置き場所とパネル幅の両方を書く（項目7）。<c>_persist</c> から呼ぶ。
    /// </summary>
    private void SavePlacementAndPanes()
    {
        SavePaneWidths();
        Placements?.Save(_placement);
    }

    /// <summary>仕切りをつまんで離した。パネル幅を落ち着いてから書く側へ仕切り直す。</summary>
    private void OnPaneResized(object sender, MouseButtonEventArgs e) => _persist.Poke();

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

        // つまんでいるあいだはスライドを引っ込めない。狭める向きに引くと、
        // つまんでいる手そのものが窓の外へ出る
        vm.Shell.IsResizing = true;

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

    /// <summary>
    /// 掴みしろの上でホイールを回したら、日を送る。
    /// <para>
    /// 細い帯では、ここが指を置きやすい場所になる。何も起きないと、送り方が
    /// 無いように見える。
    /// </para>
    /// </summary>
    private void OnGripWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || ViewModel is not { } vm) return;

        var command = e.Delta > 0 ? vm.PreviousCommand : vm.NextCommand;

        if (!command.CanExecute(null)) return;

        command.Execute(null);
        e.Handled = true;
    }

    private void OnGripReleased(object sender, MouseButtonEventArgs e)
    {
        if (_gripFrom is null) return;

        _gripFrom = null;

        if (ViewModel is { } vm) vm.Shell.IsResizing = false;

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

    // ------------------------------------------------------------------
    // 開く演出（滑り出し）のあいだ、中身の幅を固定する
    //
    // ShellController から Root へ直接触るのは筋が悪いので、ISlideRevealHost 越しに
    // ここへ頼んでもらう。窓の Width が演出で動いても、Root の幅を定位置に固定して
    // 寄せている辺へ寄せておけば、中身のレイアウト（月・年ビューなど）は
    // 組み直されない。演出が終わったら、忘れずに元（Stretch・Auto幅）へ戻す。
    // 戻し忘れると、そのあと利用者が幅をつまんで変えても中身が追従しなくなる。
    // ------------------------------------------------------------------

    void ISlideRevealHost.BeginSlideReveal(double restingWidth, DockEdge edge)
    {
        Root.Width = restingWidth;
        Root.HorizontalAlignment = edge == DockEdge.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
    }

    void ISlideRevealHost.EndSlideReveal()
    {
        Root.Width = double.NaN;
        Root.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>検索の結果を押したら、その日へ移って開く。</summary>
    private void OnSearchResultClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SearchResultViewModel found) return;

        ViewModel?.OpenSearchResult(found);
        e.Handled = true;
    }

    /// <summary>
    /// クイック入力は Enter で入れる。Esc は打ちかけを消してフォーカスを外す（項目17）。
    /// <para>検索は Esc で消せるのに、クイック入力にはそれが無かった。</para>
    /// </summary>
    private void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ViewModel is { } vm) vm.QuickText = string.Empty;

            // フォーカスを外さないと、単独キーのショートカット（T・1〜5 など）が
            // 引き続き奪われたままになる
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } main || !main.QuickCommand.CanExecute(null)) return;

        main.QuickCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 本体のキー割当（項目3）。
    /// <para>
    /// 修飾キー無しの単独キー（T・1〜5・矢印・PageUp・PageDown）は、クイック入力欄・
    /// 検索欄・エディタの中で打ったときにまで <c>Window.InputBindings</c> に奪われると
    /// 実害が出る（「3」と打つと月表示に切り替わる、など）。フォーカスが
    /// テキスト入力系の要素にあるときは、ここで <c>Handled</c> にして先に止め、
    /// InputBindings まで届かせない（素通しして、いつもどおり文字として入力させる）。
    /// </para>
    /// <para>
    /// Ctrl＋F（検索）・Ctrl＋L と /（クイック入力）はフォーカス移動そのものなので、
    /// ViewModel のコマンドにはせず、ここで直に受ける。
    /// </para>
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inTextInput = Keyboard.FocusedElement is TextBoxBase or ComboBox;

        if (inTextInput && IsBareShortcutKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        // Esc でスライドを引っ込める（項目9）。検索欄・クイック入力は自分の Esc
        // （打ちかけを消す・検索を消す）を持っているので、そちらを優先して奪わない。
        // ウィンドウ居かたでは意味が無いので ShellViewModel.RequestRetract 側で弾く。
        // 実際に画面を動かすのは ShellController（App 側）で、マウスが外れたときと
        // 同じ経路を通る
        if (!inTextInput && e.Key == Key.Escape
            && ViewModel is { } shellVm && shellVm.Shell.Mode != ShellMode.Window)
        {
            shellVm.Shell.RequestRetract();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // 畳んでいる（虫めがねだけの）ときは、先に入力欄を開かないと
            // Visibility="Collapsed" のままでフォーカスが乗らない
            if (ViewModel is { UsesCompactSearch: true } vm && vm.OpenSearchCommand.CanExecute(null))
            {
                vm.OpenSearchCommand.Execute(null);
            }

            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }, DispatcherPriority.Input);

            e.Handled = true;
            return;
        }

        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusQuickInput();
            e.Handled = true;
            return;
        }

        // 「/」は単独キーなので、テキスト入力中は普通に打たせる
        if (!inTextInput && e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.None)
        {
            FocusQuickInput();
            e.Handled = true;
        }
    }

    /// <summary>InputBindings に登録してある、修飾キー無しの単独キー。</summary>
    private static bool IsBareShortcutKey(Key key) => key is
        Key.T or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5
        or Key.Left or Key.Right or Key.PageUp or Key.PageDown;

    /// <summary>
    /// クイック入力へフォーカスする（項目4）。
    /// <para>
    /// 右ペインが開いていればそちらへ、閉じていてスリムパネルが開いていればそちらへ。
    /// どちらも閉じていれば右ペインを開いてから当てる。
    /// </para>
    /// <para>
    /// トレイの Ctrl＋Alt＋N（<c>App.xaml.cs</c>）と、本体の Ctrl＋L・/ の両方から呼ぶ。
    /// </para>
    /// </summary>
    public void FocusQuickInput()
    {
        if (ViewModel is not { } vm) return;

        if (!vm.IsDetailPaneOpen && !vm.IsSlimPanelOpen) vm.IsDetailPaneOpen = true;

        // パネルの開閉直後は、まだ幅が 0 のままでフォーカスを受け取れないことがある。
        // レイアウトが一段落してから当てる
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.IsDetailPaneOpen)
            {
                QuickInputBox.Focus();
                Keyboard.Focus(QuickInputBox);
            }
            else if (vm.IsSlimPanelOpen)
            {
                Sidebar.FocusQuickInput();
            }
        }, DispatcherPriority.Input);
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
