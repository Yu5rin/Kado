using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.App.Shell;

/// <summary>
/// 開く演出（滑り出し）のあいだ、窓の中身側に触れるための小さな窓口。
/// <para>
/// <see cref="ShellController"/> は <c>Window</c> 型で窓を受け取っているので、
/// 中身（<c>Root</c> グリッドなど）へ直接触るのは筋が悪い。<c>MainWindow</c>
/// （<c>.xaml.cs</c>）側にこの実装を持たせ、ここ越しに頼む。
/// </para>
/// </summary>
internal interface ISlideRevealHost
{
    /// <summary>
    /// 演出のあいだだけ、中身の幅を定位置の幅に固定し、寄せている辺へ寄せる。
    /// <para>
    /// 窓の <c>Width</c> が動いても中身のレイアウトが組み直されないようにする。
    /// 組み直されると、幅が変わるたびに月・年ビューが測り直されて重くなる
    /// （過去に実際に踏んだ重さの問題）。
    /// </para>
    /// </summary>
    /// <param name="restingWidth">定位置（このあと変わらない）の幅。</param>
    /// <param name="edge">寄せている辺。中身を寄せる向きに使う。</param>
    void BeginSlideReveal(double restingWidth, DockEdge edge);

    /// <summary>
    /// 固定を解く。中身の幅を自動（画面いっぱい）に戻す。
    /// <para>
    /// 呼び忘れると、そのあと利用者が幅をつまんで変えても中身が追従しなくなる
    /// （過去に実際に踏んだ形の不具合）。
    /// </para>
    /// </summary>
    void EndSlideReveal();
}

/// <summary>
/// 居かたの切り替えを実際に画面へ効かせる（要件書 2章）。
/// <para>
/// <see cref="ShellViewModel"/> が持つのは「どうしたいか」だけ。ここが AppBar の
/// 登録・解除、ホットゾーンの張り方、ウィンドウの形を受け持つ。
/// </para>
/// </summary>
public sealed class ShellController : IDisposable
{
    /// <summary>
    /// 幅を変えたあと、これだけ手が止まったら交渉し直す。
    /// <para>
    /// ワークエリアを変えると他のプロセスのウィンドウまで並び替わる。ドラッグの
    /// あいだ毎フレーム呼ぶと、画面じゅうが震えて使い物にならない（要件書 7.2）。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 滑り出る時間。
    /// <para>
    /// 速すぎると出たことに気づけず、遅いと待たされる。減速を効かせて、止まる
    /// ところを柔らかくする。
    /// </para>
    /// </summary>
    private static readonly Duration SlideInTime = new(TimeSpan.FromMilliseconds(220));

    /// <summary>引っ込む時間。出るときより短くする。用が済んだものを見送らない。</summary>
    private static readonly Duration SlideOutTime = new(TimeSpan.FromMilliseconds(160));

    /// <summary>
    /// 開く演出のイージング。<see cref="SlideInTime"/> と合わせて1か所にまとめておく。
    /// 実機で見て調整が要るかもしれない。
    /// </summary>
    private static readonly QuinticEase SlideRevealEasing = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// 開く演出の開始幅。
    /// <para>
    /// 0 だと WPF が嫌がる場面があるので 1 にする。<c>Window.MinWidth</c>
    /// （既定 220px）がそのままだとここで頭打ちになるので、演出のあいだだけ
    /// <see cref="SlideIn"/> 側で下げる。
    /// </para>
    /// </summary>
    private const double SlideRevealStartWidth = 1.0;

    private readonly Window _window;
    private readonly ShellViewModel _shell;
    private readonly DockPlacementStore _store;
    private readonly AppBarHost _appBar;
    private readonly EdgeHotZone _hotZone;
    private readonly DispatcherTimer _resizeSettle;

    /// <summary>
    /// 開く演出のあいだ、窓の中身側（<c>MainWindow</c>）に触れるための窓口。
    /// <para>
    /// 実装している型（<c>MainWindow</c>）でなければ <c>null</c> になり、そのときは
    /// 中身の固定を諦めて窓の <c>Width</c> だけ動かす。
    /// </para>
    /// </summary>
    private readonly ISlideRevealHost? _revealHost;

    /// <summary>ウィンドウモードに戻すときの姿。端へ寄せる前に控える。</summary>
    private WindowPlacement? _windowed;

    /// <summary>
    /// ピン留めする前の作業領域。
    /// <para>
    /// <b>外した直後に測ってはいけない。</b>ワークエリアの変更はすぐには行き渡らず、
    /// 解除の直後はまだ削られたままの値が返る。それを画面端の基準に使うと、
    /// 削っていた幅ぶん内側――つまり画面の真ん中にスライドが出る。
    /// </para>
    /// </summary>
    private Rect? _workBeforeDock;

    private bool _disposed;

    /// <summary>いま引っ込んでいる最中。終わるまで重ねて呼ばない。</summary>
    private bool _slidingOut;

    /// <summary>いま開く演出（Width を広げているところ）の最中。</summary>
    private bool _revealing;

    /// <summary>
    /// 開く演出のあいだだけ下げる <c>Window.MinWidth</c>。演出前の値をここへ控えておき、
    /// 演出が終わる・打ち切られるときに戻す。
    /// </summary>
    private double _minWidthBeforeReveal;

    /// <summary>
    /// 右に寄せているときの開く演出が使う、毎フレームの <c>CompositionTarget.Rendering</c>
    /// ハンドラ。演出中だけ入っており、打ち切り（<see cref="CancelReveal"/>）でも
    /// 必ずここから外す。外し忘れると、引っ込めたあとも窓を動かし続ける。
    /// </summary>
    private EventHandler? _revealRenderingHandler;

    /// <summary>
    /// スライドから、カーソルが外れたら引っ込めるか。
    /// <para>
    /// 既定は引っ込める。用があるときだけ出てくるのがスライドの形で、出したまま
    /// にしたければピンで留める。設定で切れる。
    /// </para>
    /// </summary>
    public bool SlideOutOnLeave { get; set; } = true;

    public ShellController(Window window, ShellViewModel shell, DockPlacementStore store)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _appBar = new AppBarHost(window, store);
        _hotZone = new EdgeHotZone();

        // 開く演出のあいだ、中身の幅を固定してもらう窓口。MainWindow でなければ
        // null になり、そのときは窓の Width だけ動かして中身の固定は諦める
        _revealHost = window as ISlideRevealHost;

        _resizeSettle = new DispatcherTimer { Interval = ResizeSettle };
        _resizeSettle.Tick += (_, _) =>
        {
            _resizeSettle.Stop();
            _appBar.Resize(_shell.DockWidth);
        };

        _shell.ModeChanged += (_, mode) => Apply(mode);
        _shell.EdgeChanged += (_, _) => Apply(_shell.Mode);
        _shell.DockWidthChanged += (_, _) =>
        {
            if (!_shell.IsPinned) { ApplyOverlayBounds(); return; }

            // 窓の幅だけ先に動かす。交渉が済むまで何も動かないと、掴んで引いても
            // びくともしないように見えて「幅を変えられない」となる
            ApplyPinnedWidth();

            // 作業領域の取り合いは、手が止まってから
            _resizeSettle.Stop();
            _resizeSettle.Start();
        };

        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ShellViewModel.IsResizing)) return;

            // つまんでいるあいだは AppBarHost 側のガードを外す。ApplyPinnedWidth は
            // 交渉前（_confirmed が古いまま）に窓を動かすので、ガードを効かせたままだと
            // 古い確定値へ押し戻されて「幅をつまんで変えられない」が再発する
            _appBar.SuppressGuard = _shell.IsResizing;

            if (_shell.IsResizing) return;

            // つまみ終えた。留めているなら、待たずに譲る幅を決め直す。
            // 隣のアプリはワークエリアを見て並ぶので、ここで初めて動く
            if (_shell.IsPinned)
            {
                _resizeSettle.Stop();
                _appBar.Resize(_shell.DockWidth);
                return;
            }

            // スライドなら、見張る範囲を今の姿に合わせ直す
            if (!SlideOutOnLeave || _shell.Mode != ShellMode.Overlay || !_window.IsVisible) return;

            _hotZone.WatchLeaving(WindowRectAt(_window.Left));
        };

        // 帯に留まったらスライドさせる
        _hotZone.Triggered += (_, _) => SlideIn();

        // 窓から外れたら引っ込める。押そうとしたボタンが逃げないよう、外れてから
        // 少し置いてから来る。CancelReveal は、開く演出の途中に割り込まれたときの
        // 後始末（下記 Deactivated の配線と同じ理由）
        _hotZone.Left += (_, _) =>
        {
            CancelReveal();
            SlideOutIfIdle();
        };

        // 他のアプリへ移ったら引っ込める。スライドは「用があるときだけ出る」もので、
        // 出しっぱなしにしたいならピンで留める。
        //
        // 判定は1パス遅らせる（不具合3）。Win32 の WM_ACTIVATE は、先に非活性になる
        // 窓へ WA_INACTIVE を送る。WPF の HandleActivate はその中で同期的に
        // IsActive=false → OnDeactivated を起こすので、Deactivated の時点では
        // まだどの窓も IsActive になっていない。相手（予定追加の編集画面など）の
        // IsActive=true はそのあとの WA_ACTIVE で立つ。次のディスパッチまで待ち、
        // それでも戻っていなければ本当に前面を譲ったと判断する
        _window.Deactivated += (_, _) =>
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_window.IsActive) return;   // すぐ戻ってきた

                // 開く演出（220ms）の途中で他のアプリへ切り替えられることがある。
                // 引っ込み自体（SlideOutIfIdle）は変えず、その手前で演出だけ打ち切る
                CancelReveal();

                // メニュー・ポップアップの「開いている数」を保険として無視する
                // （項目3）。数え漏れて0に戻らなかったときの保険で、ここまで
                // 塞ぐと二度と引っ込まなくなる。他アプリへ本当に切り替わった
                // なら、開いていたメニュー・ポップアップは自然に閉じているはず
                // （WPF は非活性になった窓の ContextMenu／Popup を自動で閉じる）
                // なので、ここだけは無視しても実害が無い
                SlideOutIfIdle(ignorePopups: true);
            }));

        // 全画面アプリなどで外れたら、見た目も合わせる
        _appBar.Undocked += (_, _) =>
        {
            if (_shell.IsPinned) _shell.Mode = ShellMode.Overlay;
        };

        // Esc などキー操作での引っ込め（要件書外だが実機の使い勝手として足した）。
        // マウスが外れたときと同じ経路（SlideOutIfIdle）を通す。独自の経路は作らない。
        // 開いた直後に Esc を打たれることもあるので、CancelReveal を挟む
        _shell.RetractRequested += (_, _) =>
        {
            CancelReveal();
            SlideOutIfIdle();
        };

        // 起動時に1回、モニタ構成を shell.log へ残す（不具合1の切り分け用）
        LogStartupScreens();
    }

    /// <summary>
    /// 画面を分割できなかった。
    /// <para>黙って諦めると「ピンを押しても何も起きない」としか見えない。</para>
    /// </summary>
    public event EventHandler<string>? DockFailed;

    /// <summary>起動時に、控えてあった居かたへ戻す。</summary>
    public void Restore() => Apply(_shell.Mode);

    /// <summary>前に出す。トレイやホットキーからも呼ぶ。</summary>
    public void Show()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        _window.Activate();

        RaiseToTopIfOverlay();
    }

    /// <summary>
    /// 帯として出しているあいだ、窓の並びをいちばん手前へ入れ直す。
    /// <para>
    /// <c>Window.Topmost</c> は <see cref="ToEdge"/> で true にしてあるが、それだけでは
    /// <b>他のウィンドウの下に出ることがある</b>。隠して出し直す作りなので、出し直した
    /// 拍子に実際の並びが Topmost のとおりでなくなる。出すたびにここで入れ直す。
    /// </para>
    /// <para>
    /// 位置も大きさも変えず、前面も奪わない（<c>SWP_NOACTIVATE</c>）。相手のアプリで
    /// 文字を打っている最中に呼ばれても、入力の行き先は変わらない。
    /// </para>
    /// <para>
    /// ピンで留めているあいだ（<see cref="ShellMode.Dock"/>）は呼ばない。あちらは
    /// 作業領域そのものを分けてもらっていて、<c>Topmost</c> を落とすのが正しい
    /// （落とさないと、シェルが重なった窓を押し出す）。
    /// </para>
    /// </summary>
    private void RaiseToTopIfOverlay()
    {
        if (_shell.Mode != ShellMode.Overlay) return;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 滑り出させて出す。
    /// <para>
    /// <b>窓は最初から定位置（寄せている辺）にあり、<c>Width</c> を広げて
    /// 「めくれるように」見せる。</b>画面の外から <c>Left</c> を動かして
    /// 滑り込ませていた前の作りは、窓の右端（寄せている辺と逆側）が先に画面へ
    /// 入ってしまい、いちばん先に読みたい左端の中身が最後に到着するうえ、
    /// 中身が横に流れて落ち着かなかった。中身（<see cref="ISlideRevealHost"/>）の
    /// 幅は定位置に固定して端へ寄せておくので、窓の <c>Width</c> が動いても中身の
    /// レイアウトは組み直されない（月・年ビューの重さの問題を防ぐ）。
    /// </para>
    /// <para>位置を決めてから出す。出してから動かすと、一度別の場所に見えて飛ぶ。</para>
    /// <para>
    /// <b>アニメーションの開始は、出したのと同じフレームではしない。</b>
    /// <see cref="Show"/>／<see cref="Window.Activate"/> の直後に始めると、
    /// 窓が現れる処理と競合して開始値が拾われず、いきなり最終位置に出てしまう
    /// （実機で確認済み。引っ込み側 <see cref="SlideOutIfIdle"/> はこの競合が無く、
    /// 完璧に動いている）。窓が出きったあとのフレーム（<see cref="DispatcherPriority.Loaded"/>）
    /// まで待ってから始める。
    /// </para>
    /// </summary>
    private void SlideIn()
    {
        if (_shell.Mode != ShellMode.Overlay) return;

        // もう出ている。飛ばして開き直さない（不具合1）。
        //
        // 出たあとも帯を見張り続けていて（Apply(Overlay) が WatchLeaving を呼んで
        // いなかった）、カーソルが帯に留まるたびにここへ入り、見えている窓を
        // 画面外へ飛ばしてから滑り直していた。SlideOutOnLeave=false のときは
        // 約350msごとに繰り返す
        if (_window.IsVisible && !_slidingOut) return;

        ApplyOverlayBounds();

        // 定位置（このあと動かさない値）を、Width を縮める前に確定しておく
        var restingLeft = _window.Left;
        var restingWidth = _window.Width;

        // 見張る矩形は「定位置」の矩形で取る。縮めたあとの値で取ると、出た直後に
        // カーソルが窓の外と判定されて引っ込む（過去に踏んだ不具合）
        var shown = WindowRectAt(restingLeft);

        // 中身の幅を定位置ぶんに固定し、寄せている辺へ寄せておく。窓の Width が
        // 動いても中身のレイアウトが組み直されないようにする。.xaml には触れないので
        // MainWindow 側の窓口（ISlideRevealHost）越しに頼む
        _revealHost?.BeginSlideReveal(restingWidth, _shell.Edge);
        _revealing = true;

        // 窓をいったん畳んでおく。0 だと WPF が嫌がる場面があるので 1 にする。
        // MinWidth がそのままだとそこで頭打ちになるので、演出のあいだだけ下げる
        _minWidthBeforeReveal = _window.MinWidth;
        _window.MinWidth = SlideRevealStartWidth;
        _window.Width = SlideRevealStartWidth;

        // 右に寄せているときは、右端を定位置のまま保つよう Left も詰める。
        // 左に寄せているときは Left はもう動かさない（ShellGeometry.RevealLeft）
        _window.Left = ShellGeometry.RevealLeft(_shell.Edge, restingLeft, restingWidth, SlideRevealStartWidth);

        Show();
        LogWindowRect("Show直後");

        // Show() の直後、同じフレームでアニメーションを始めない。窓が出る処理と
        // 競合して開始値が拾われないことがあるため、出きった次のフレームまで待つ
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // 待っているあいだに引っ込め始めた・出しかたが変わったなら、もう動かさない
            if (_shell.Mode != ShellMode.Overlay || !_window.IsVisible || _slidingOut)
            {
                CancelReveal();
                return;
            }

            // Show() のあとで畳んだ値からずれていないか確かめ、ずれていたら戻す。
            // ここで戻さないと「一瞬だけ最終位置に見えてから飛ぶ」、あるいは
            // 動かないまま最終位置に出る、という形で症状が出る
            var startWidth = Math.Max(SlideRevealStartWidth, _window.MinWidth);
            if (Math.Abs(_window.Width - startWidth) > 0.5) _window.Width = startWidth;

            var expectedLeft = ShellGeometry.RevealLeft(_shell.Edge, restingLeft, restingWidth, startWidth);
            if (Math.Abs(_window.Left - expectedLeft) > 0.5) _window.Left = expectedLeft;

            LogWindowRect("アニメーション開始直前");

            // 開始値は現在値まかせにせず、畳んだ幅と定位置の左端を明示して渡す
            AnimateReveal(startWidth, restingWidth, restingLeft,
                () =>
                {
                    _revealHost?.EndSlideReveal();

                    // 滑り出しているあいだに他のアプリが前面を取ると、出きった時点で
                    // 下に潜っていることがある。終わりにもう一度入れ直す
                    RaiseToTopIfOverlay();

                    LogWindowRect("Animate完了");
                });
        }));

        // 出たあとは、外れるのを見張る番
        if (SlideOutOnLeave) _hotZone.WatchLeaving(shown);
    }

    /// <summary>
    /// 引っ込める。
    /// <para>
    /// スライド中だけ。ピンで留めているあいだは、他のアプリへ移っても出したままにする。
    /// </para>
    /// </summary>
    /// <param name="ignorePopups">
    /// <c>true</c> なら、メニュー・ポップアップが開いていても構わず引っ込める。
    /// <para>
    /// 他アプリへ実際に切り替わったとき（<see cref="Window.Deactivated"/> 経由）だけ
    /// 渡す保険（項目3）。<see cref="PopupActivityHooks"/> の数え漏れ（Opened は
    /// 拾えたのに何らかの理由で Closed が来なかった場合）で
    /// <see cref="PopupActivityHooks.Tracker"/> が0に戻らなくなっても、この経路は
    /// 塞がれずに残る。数え漏れの逆側（「二度と引っ込まない」）を起こさないための
    /// 保険で、既定は <c>false</c>（開いていれば引っ込めない）。
    /// </para>
    /// </param>
    private void SlideOutIfIdle(bool ignorePopups = false)
    {
        if (_shell.Mode != ShellMode.Overlay) return;

        if (_slidingOut || !_window.IsVisible) return;

        // 幅をつまんでいる最中。手が窓の外に出ていても引っ込めない
        if (_shell.IsResizing) return;

        // 自分が出したメニュー・ポップアップが開いている最中。狭いスライドでは
        // メニューが窓の右端をはみ出し、その上へカーソルを動かすとホットゾーンが
        // 「窓から外れた」と判定していた。開いているあいだは引っ込めない（項目3）
        if (!ignorePopups && PopupActivityHooks.Tracker.IsAnyOpen) return;

        // 自分が出した窓（編集画面など）に移っただけなら、引っ込めない。
        // 予定を書いている最中に本体が消えると、書き終わって戻る先が無くなる
        if (OwnsForeground()) return;

        var resting = _window.Left;
        _slidingOut = true;

        // 開始値は現在値まかせ（From を渡さない）のまま。引っ込みは元からこの形で
        // 完璧に動いているので、挙動を変えない
        Animate(null, OffScreenLeft(resting), SlideOutTime, new QuadraticEase { EasingMode = EasingMode.EaseIn },
            () =>
            {
                _slidingOut = false;
                _window.Hide();

                // 次に出すときのために、居場所は戻しておく
                _window.Left = resting;
                _hotZone.WatchEdge();
            });
    }

    /// <summary>
    /// 画面の外に置いたときの左端。
    /// <para>
    /// <paramref name="resting"/>（休止位置）を基準にする。モニタの取り違えや
    /// 倍率のずれがあっても、寄せている辺の向こう側にしか行かない（不具合1）。
    /// 計算そのものは <see cref="ShellGeometry.OffScreenLeft"/> に切り出してある
    /// （<c>Window</c> に依存しないのでテストできる）。
    /// </para>
    /// </summary>
    private double OffScreenLeft(double resting)
    {
        var scale = Scale();
        var screen = ScreenOfWindow();

        // 実測（_window.Width）ではなく定位置の幅（DockWidth）を使う。開く演出の
        // あいだは Width が途中の値を取るので、それを拾うと画面外へ出す距離を
        // 取り違える（引っ込みは Width を動かさないので普段は同じ値になる）
        var width = _shell.DockWidth;

        var offScreen = ShellGeometry.OffScreenLeft(_shell.Edge, resting, width, screen, scale);

        ShellDiagnosticsLog.Write(
            $"OffScreenLeft edge={_shell.Edge} dockWidth={width:F1} windowWidth={_window.Width:F1} " +
            $"screen=({screen.left},{screen.top},{screen.right},{screen.bottom}) scale={scale:F2} " +
            $"resting={resting:F1} offScreen={offScreen:F1}");

        return offScreen;
    }

    /// <summary>いまの <c>_window.Left</c> と、Windows 自身が答える実際の矩形を記録する。</summary>
    private void LogWindowRect(string phase)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            var actual = handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var rect)
                ? $"{rect.left},{rect.top},{rect.right},{rect.bottom}"
                : "取得不可";

            ShellDiagnosticsLog.Write($"SlideIn {phase} Left={_window.Left:F1} GetWindowRect=({actual})");
        }
        catch
        {
            // 記録できなくても、動作は止めない
        }
    }

    /// <summary>
    /// 起動時に1回、モニタ構成を控える。
    /// <para>
    /// 不具合1（反対側から出る）は原因が確定していない。実機の値を <c>shell.log</c> に
    /// 残せるようにしておく。
    /// </para>
    /// </summary>
    private static void LogStartupScreens()
    {
        try
        {
            var monitors = new List<string>();

            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT rect, IntPtr _) =>
                {
                    var dpi = "?";
                    try
                    {
                        if (NativeMethods.GetDpiForMonitor(
                                hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0)
                        {
                            dpi = $"{dpiX}x{dpiY}";
                        }
                    }
                    catch
                    {
                        // 古い Windows などで取れなくても、矩形だけは残す
                    }

                    monitors.Add($"[{rect.left},{rect.top},{rect.right},{rect.bottom}]@{dpi}");
                    return true;
                }, IntPtr.Zero);

            ShellDiagnosticsLog.Write(
                $"startup virtualScreen=({SystemParameters.VirtualScreenLeft:F0}," +
                $"{SystemParameters.VirtualScreenWidth:F0}) primaryScreenWidth=" +
                $"{SystemParameters.PrimaryScreenWidth:F0} monitors=[{string.Join(" ", monitors)}]");
        }
        catch
        {
            // 記録できなくても、起動は止めない
        }
    }

    /// <summary>
    /// 横に滑らせる。<c>SlideOutIfIdle</c>（引っ込み）が使う。この動きは利用者が
    /// 「完璧」と言っている挙動なので変えていない。
    /// <para>
    /// <b>終わったらアニメーションを外す。</b>掛けたままだと、そのあと
    /// <c>Left</c> に入れた値が効かなくなる（アニメーションが値を握り続ける）。
    /// </para>
    /// <para>
    /// <paramref name="from"/> を渡さなければ、呼んだ時点の現在値を開始値にする
    /// （引っ込み側はこちら）。渡せば、その値を開始値として明示する。
    /// </para>
    /// </summary>
    private void Animate(double? from, double to, Duration time, IEasingFunction easing, Action? done = null)
    {
        var animation = from.HasValue
            ? new DoubleAnimation(from.Value, to, time) { EasingFunction = easing }
            : new DoubleAnimation(to, time) { EasingFunction = easing };

        animation.Completed += (_, _) =>
        {
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.Left = to;
            done?.Invoke();
        };

        _window.BeginAnimation(Window.LeftProperty, animation);
    }

    /// <summary>
    /// 幅を「めくれるように」広げる（開く演出）。
    /// <para>
    /// 中身は <see cref="ISlideRevealHost.BeginSlideReveal"/> で固定してあるので、
    /// ここは窓の <c>Width</c>（右に寄せているときは <c>Left</c> も）を動かすだけでよい。
    /// </para>
    /// <para>
    /// <b>左に寄せているときは、これまでどおり WPF の <c>DoubleAnimation</c> で
    /// <c>Width</c> だけを動かす。</b>右に寄せているときだけ話が別で、
    /// <see cref="AnimateRevealRight"/> に任せる（下記コメント参照）。
    /// </para>
    /// </summary>
    private void AnimateReveal(double fromWidth, double toWidth, double restingLeft, Action? done)
    {
        ShellDiagnosticsLog.Write(
            $"SlideIn 開く演出 edge={_shell.Edge} widthFrom={fromWidth:F1} widthTo={toWidth:F1} " +
            $"dockWidth={_shell.DockWidth:F1} restingLeft={restingLeft:F1}");

        if (_shell.Edge == DockEdge.Right)
        {
            AnimateRevealRight(fromWidth, toWidth, restingLeft, done);
            return;
        }

        var widthAnimation = new DoubleAnimation(fromWidth, toWidth, SlideInTime)
        {
            EasingFunction = SlideRevealEasing,
        };

        widthAnimation.Completed += (_, _) =>
        {
            _window.BeginAnimation(Window.WidthProperty, null);
            _window.Width = toWidth;
            EndReveal();
            done?.Invoke();
        };

        _window.BeginAnimation(Window.WidthProperty, widthAnimation);
    }

    /// <summary>
    /// 右に寄せているときの開く演出。
    /// <para>
    /// <b>WPF の <c>DoubleAnimation</c> は使わない。</b><c>Window.LeftProperty</c> と
    /// <c>Window.WidthProperty</c> に別々のアニメーションを掛けると、WPF はこの2つを
    /// 別々の <c>SetWindowPos</c> でウィンドウへ反映する。1コマごとに「Left だけ動いて
    /// 右端が壁から離れる」中間状態が挟まり、右端が壁に貼り付いたまま伸びるように
    /// 見えなくなる（引っ込むほう・左に寄せているときのどちらも Left か Width の
    /// 片方しか動かさないので、この問題は起きない）。
    /// </para>
    /// <para>
    /// 代わりに <see cref="System.Windows.Media.CompositionTarget.Rendering"/> で
    /// 毎コマ呼ばれる処理を作り、経過時間から進み具合 t（0〜1）を出して
    /// <see cref="SlideRevealEasing"/> で緩急を付け、いまの幅を求める。右端
    /// （<paramref name="restingLeft"/> + <paramref name="toWidth"/>）を物理ピクセルで
    /// 固定し、<c>x = 右端 − 幅</c> として位置と幅を <b>1回の <c>SetWindowPos</c> に
    /// まとめて</b>渡す。これで「Left だけ動く」中間状態が生まれない。
    /// </para>
    /// <para>
    /// <b>打ち切り（<see cref="CancelReveal"/>）でも必ず外す。</b>
    /// <see cref="_revealRenderingHandler"/> に控えておき、外し忘れて窓を動かし続ける
    /// ことがないようにする。
    /// </para>
    /// </summary>
    private void AnimateRevealRight(double fromWidth, double toWidth, double restingLeft, Action? done)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            // ハンドルがまだ無い（Show() のあとにしか来ないので、実機ではまず起きない）。
            // 演出は省いて、いきなり定位置に置く。WPF のアニメーションで代用すると、
            // まさに直したかった「右端が壁から離れる」動きに戻ってしまう
            _window.Left = restingLeft;
            _window.Width = toWidth;
            EndReveal();
            done?.Invoke();
            return;
        }

        // 前の演出の処理が残っていれば外してから登録する。二重に走ると、
        // 2本の処理が同じ窓を奪い合って位置が暴れる
        if (_revealRenderingHandler is { } previous)
        {
            System.Windows.Media.CompositionTarget.Rendering -= previous;
            _revealRenderingHandler = null;
        }

        var scale = Scale();
        var topPhysical = (int)Math.Round(_window.Top * scale);
        var heightPhysical = (int)Math.Round(_window.Height * scale);
        var rightEdgePhysical = (int)Math.Round((restingLeft + toWidth) * scale);
        var totalMs = SlideInTime.TimeSpan.TotalMilliseconds;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        void OnRendering(object? sender, EventArgs e)
        {
            var t = totalMs <= 0 ? 1.0 : Math.Min(1.0, stopwatch.Elapsed.TotalMilliseconds / totalMs);
            var eased = SlideRevealEasing.Ease(t);
            var width = fromWidth + ((toWidth - fromWidth) * eased);
            var widthPhysical = (int)Math.Round(width * scale);
            var xPhysical = rightEdgePhysical - widthPhysical;

            NativeMethods.SetWindowPos(
                handle, IntPtr.Zero, xPhysical, topPhysical, widthPhysical, heightPhysical,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

            if (t < 1.0) return;

            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            _revealRenderingHandler = null;

            _window.Left = restingLeft;
            _window.Width = toWidth;
            EndReveal();
            done?.Invoke();
        }

        _revealRenderingHandler = OnRendering;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// 開く演出を打ち切る。演出中でなければ何もしない。
    /// <para>
    /// <see cref="SlideOutIfIdle"/> 自体は変えない（引っ込みは完璧に動いている）。
    /// 呼び出し側でここを先に呼ぶことで、<see cref="SlideInTime"/>（220ms）より
    /// 短い間隔で引っ込みが割り込んでも、広がる演出と画面外へ動く演出が重ならない
    /// ようにする。
    /// </para>
    /// </summary>
    private void CancelReveal()
    {
        if (!_revealing) return;

        EndReveal();
        _window.BeginAnimation(Window.WidthProperty, null);
        _window.BeginAnimation(Window.LeftProperty, null);

        // 右に寄せているときの開く演出は WPF のアニメーションではなく
        // CompositionTarget.Rendering を使っている。外し忘れると、引っ込めた
        // あとも毎フレーム SetWindowPos を呼び続けて窓を動かし続けてしまう
        if (_revealRenderingHandler is { } handler)
        {
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            _revealRenderingHandler = null;
        }

        _revealHost?.EndSlideReveal();
    }

    /// <summary>演出中フラグを下ろし、演出向けに下げていた <c>MinWidth</c> を元に戻す。</summary>
    private void EndReveal()
    {
        _revealing = false;
        _window.MinWidth = _minWidthBeforeReveal;
    }

    /// <summary>滑りを止めて、位置を自分の手に戻す。</summary>
    private void StopSliding()
    {
        _slidingOut = false;
        _window.BeginAnimation(Window.LeftProperty, null);

        // 開く演出（Width、右寄せなら Left も）が残っていたら、ここで打ち切る。
        // 掛けたままだと、このあと Width へ入れる値が効かなくなる
        CancelReveal();
    }

    /// <summary>
    /// その左端に置いたときの窓の矩形（物理ピクセル）。
    /// <para>カーソルが窓から外れたかを見るのに使う。</para>
    /// </summary>
    private NativeMethods.RECT WindowRectAt(double left)
    {
        var scale = Scale();

        return new NativeMethods.RECT
        {
            left = (int)Math.Round(left * scale),
            top = (int)Math.Round(_window.Top * scale),
            right = (int)Math.Round((left + _window.Width) * scale),
            bottom = (int)Math.Round((_window.Top + _window.Height) * scale),
        };
    }

    /// <summary>画面の倍率。Win32 はピクセル、WPF は倍率を割った値で話す。</summary>
    private double Scale() =>
        PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    /// <summary>いま窓が乗っているモニタ全体（物理ピクセル）。</summary>
    private NativeMethods.RECT ScreenOfWindow()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;

        return Screens.Of(handle, Scale());
    }

    /// <summary>
    /// いま窓が乗っているモニタの作業領域（DIP）。
    /// <para>
    /// <b>メインディスプレイ固定の <c>SystemParameters.WorkArea</c> をそのまま使わない
    /// （不具合1・複数モニタ対策）。</b>会社のような複数モニタでメイン以外へ寄せて
    /// 使うと、スライドの位置・高さがまるごと別の画面の値になる。窓がまだどこにも
    /// 出ていなければ <see cref="Screens.WorkOf"/> 側の代用（プライマリ画面）に任せる。
    /// </para>
    /// </summary>
    private Rect CurrentMonitorWorkArea()
    {
        var scale = Scale();
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        var work = Screens.WorkOf(handle, scale);

        return new Rect(work.left / scale, work.top / scale, work.Width / scale, work.Height / scale);
    }

    /// <summary>
    /// いま前にいるのが、自分の出した窓か。
    /// <para>
    /// <c>MessageBox</c>（削除の確認など）やファイル選択ダイアログは WPF の
    /// <see cref="Window"/> ではないので、<c>Application.Current.Windows</c> だけを
    /// 見る判定では決して true にならない（不具合3）。前面の窓を Win32 側から
    /// 引き直し、自分と同じプロセスの窓かで補う。
    /// </para>
    /// </summary>
    private bool OwnsForeground()
    {
        if (Application.Current?.Windows.OfType<Window>()
                .Any(w => !ReferenceEquals(w, _window) && w.IsActive) == true) return true;

        // MessageBox・ファイル選択など、WPF の Window ではない窓
        var foreground = NativeMethods.GetForegroundWindow();
        var self = new System.Windows.Interop.WindowInteropHelper(_window).Handle;

        // 自分が前面なら従来どおり（カーソルが外れたときの経路に任せる）
        if (foreground == IntPtr.Zero || foreground == self) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>いまの居場所を控える。</summary>
    public void Save() => _store.Save(_shell.Placement());

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _resizeSettle.Stop();

        // 削ったまま終わらせない。ここを通らずに落ちた場合は、
        // 次の起動で WorkAreaGuard が戻す
        _appBar.Dispose();
        _hotZone.Dispose();
    }

    private void Apply(ShellMode mode)
    {
        switch (mode)
        {
            case ShellMode.Window:
                _appBar.Undock();
                _hotZone.Disarm();
                _workBeforeDock = null;
                ToWindow();
                Show();
                break;

            case ShellMode.Overlay:
                _appBar.Undock();
                ToEdge();
                ApplyOverlayBounds();

                // 切り替えた直後は出したままにする。いきなり消えると、何が起きたのか
                // 分からない。他のアプリへ移った時点で引っ込む
                Show();

                // ピン留め中は張らない。常時出ているので呼び出す口が要らない。
                // 見張る画面は、いま窓が乗っているモニタ。出してから引くのは、
                // 隠れているあいだはハンドルがまだ無いことがあるため
                _hotZone.Arm(_shell.Edge, ScreenOfWindow());

                // 出したその場で「外れたら引っ込める」へ切り替える（不具合1）。
                //
                // ここを素通りして帯の見張りのままにすると、出ている窓の上を
                // 帯が兼ねてしまう。カーソルを帯の上（＝出ている窓の端）に
                // 置いたままにするたびに SlideIn が走り、見えている窓を
                // 画面外へ飛ばしてから滑り直していた。出しっぱなしにする
                // 設定（SlideOutOnLeave=false）のときは、これまでどおり
                // 帯の見張りのままにする（切り替えない＝勝手に引っ込まない）
                if (SlideOutOnLeave) _hotZone.WatchLeaving(WindowRectAt(_window.Left));
                break;

            case ShellMode.Dock:
                _hotZone.Disarm();

                // 削る前に控える。外したあとでは正しい値が取れない。
                // いま窓が乗っているモニタの作業領域を測る（複数モニタ対策。後述）
                _workBeforeDock ??= CurrentMonitorWorkArea();

                ToEdge();

                if (_appBar.Dock(_shell.Edge, _shell.DockWidth))
                {
                    // ABM_SETPOS が済んでから最前面を外す（不具合：ピン留め時に一瞬
                    // 右へ飛ぶ）。Windows のシェルは、作業領域から帯を削るとき、その帯に
                    // 重なる「非 Topmost の普通の窓」を作業領域の内側へ押し出す。
                    // ここより前に Topmost=false にすると、ABM_SETPOS の瞬間だけ自分の窓が
                    // まさにその条件（非 Topmost・削られる帯に重なっている）を満たし、
                    // 押し出されてから戻る、という一往復が「一瞬右へ飛ぶ」に見えていた。
                    // ドックのあいだ最前面にしない、という意図そのものは変えない。
                    // 場所を譲ってもらっているので、重ねる必要がない
                    _window.Topmost = false;
                    break;
                }

                // 削れなかった。黙って重なったままにせず、理由を伝えてから落とす。
                // Topmost はここでは触らない。この直後の Mode=Overlay が Apply(Overlay) を
                // 呼び、その ToEdge() が改めて Topmost=true にする
                DockFailed?.Invoke(this, _appBar.LastFailure ?? "画面を分割できませんでした。");
                _shell.Mode = ShellMode.Overlay;
                break;
        }
    }

    /// <summary>ふつうのウィンドウに戻す。端へ寄せる前の姿へ。</summary>
    private void ToWindow()
    {
        StopSliding();
        _window.Topmost = false;
        _window.WindowStyle = WindowStyle.SingleBorderWindow;
        _window.ResizeMode = ResizeMode.CanResize;

        if (_windowed is { } saved)
        {
            _window.Width = saved.Width;
            _window.Height = saved.Height;

            if (saved.HasPosition)
            {
                _window.Left = saved.Left;
                _window.Top = saved.Top;
            }

            _windowed = null;
        }
    }

    /// <summary>
    /// 端へ寄せる形にする。
    /// <para>
    /// 枠を消すのは、画面端に貼り付いたときにタイトルバーが場所を食うから
    /// （要件書 7.2）。戻せるよう、寄せる前の姿を控えておく。
    /// </para>
    /// </summary>
    private void ToEdge()
    {
        StopSliding();
        _windowed ??= new WindowPlacement(
            _window.Left, _window.Top, _window.Width, _window.Height, IsMaximized: false);

        if (_window.WindowState == WindowState.Maximized) _window.WindowState = WindowState.Normal;

        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.Topmost = true;
    }

    /// <summary>
    /// 留めているあいだの幅。
    /// <para>
    /// 作業領域の取り合いは重いので、ドラッグのあいだは窓の幅だけ動かしておき、
    /// 手が止まってから改めて交渉する。
    /// </para>
    /// </summary>
    private void ApplyPinnedWidth()
    {
        if (!_shell.IsPinned) return;

        var scale = Scale();
        var screen = ScreenOfWindow();
        var width = _shell.DockWidth;

        _window.Width = width;
        _window.Left = _shell.Edge == DockEdge.Left
            ? screen.left / scale
            : (screen.right / scale) - width;
    }

    /// <summary>
    /// オーバーレイの位置。
    /// <para>ワークエリアは削らないので、こちらで画面端に合わせる。</para>
    /// </summary>
    private void ApplyOverlayBounds()
    {
        if (!_shell.IsAtEdge || _shell.IsPinned) return;

        // 滑りが残っていると、ここで入れた値が効かない。アニメーションは
        // 掛けたあいだ値を握り続ける
        StopSliding();

        // 留める前に控えた値があればそちらを使い、使ったら捨てる。次に出すときには
        // ワークエリアも戻っているので、そのときは素直に測ってよい
        var work = _workBeforeDock ?? CurrentMonitorWorkArea();
        _workBeforeDock = null;

        var width = _shell.DockWidth;

        _window.Top = work.Top;
        _window.Height = work.Height;
        _window.Width = width;
        _window.Left = _shell.Edge == DockEdge.Left ? work.Left : work.Right - width;

        // 幅を変えたら、見張る範囲も合わせる。
        //
        // 出したときの矩形のまま見張っていると、掴んで広げた先にカーソルを
        // 置いた時点で「窓から外れた」ことになって引っ込む。つまり、
        // 幅を広げようとすると必ず消える――幅を変えられない、という形で出る
        if (_window.IsVisible && SlideOutOnLeave) _hotZone.WatchLeaving(WindowRectAt(_window.Left));
    }
}
