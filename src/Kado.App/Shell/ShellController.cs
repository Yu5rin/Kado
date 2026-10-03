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

    /// <summary>
    /// 画面の構成が変わったあと、これだけ続報が来なくなったら決め直す。
    /// <para>
    /// 構成の変更は続けて何度も来る（モニタが1枚ずつ現れる、倍率が切り替わる）。
    /// 作業領域もすぐには追い付かないので、少し長めに待つ。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DisplaySettle = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 置き直しを見送れる回数の上限（<see cref="DisplaySettle"/> ごとに1回）。
    /// 20回で約 14 秒。それでも演出などが終わらなければ、構わず決め直す。
    /// </summary>
    private const int MaxDisplayDeferrals = 20;

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
    /// 画面の構成（モニタの数・位置・倍率）が変わったあと、落ち着くのを待つタイマー。
    /// <para>
    /// ログオン直後は、構成が数秒〜数十秒かけて何度も変わる。<c>WM_DISPLAYCHANGE</c> と
    /// <c>WM_DPICHANGED</c> は続けて何度も来るので、そのたびに置き直さず、
    /// 来なくなってから1回だけ決め直す（<see cref="RelayoutForDisplay"/>）。
    /// </para>
    /// </summary>
    private readonly DispatcherTimer _displaySettle;

    /// <summary>落ち着くのを待っているあいだに来た、画面の構成が変わった原因。ログ用。</summary>
    private readonly List<string> _displayCauses = [];

    /// <summary>AppBar が外れたときの受け方と、終了時の片付け（終了印を消す）。</summary>
    private readonly UndockReaction _undockReaction;

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

    /// <summary>
    /// 画面の構成が変わったあとの置き直しを、演出・つまみ・交渉が済むまで見送った回数。
    /// 何かが引っかかって終わらなくても、待ち続けないための上限に使う。
    /// </summary>
    private int _displayDeferrals;

    /// <summary>直近に置き場所を決めたときの画面の構成。変わったかをログに残すため。</summary>
    private IReadOnlyList<ScreenInfo>? _lastScreens;

    /// <summary>置き直しの最中。自分の置き直しが起こした倍率の変更の知らせを、また数えないため。</summary>
    private bool _relayouting;

    /// <summary>
    /// 直前に引っ込めるのを見送った理由。同じ理由を 0.5 秒ごとに書き続けないため、
    /// 変わったときだけ <c>shell.log</c> へ書く。引っ込めたら消す。
    /// </summary>
    private string? _lastSlideOutSkip;

    /// <summary>いま引っ込んでいる最中。終わるまで重ねて呼ばない。</summary>
    private bool _slidingOut;

    /// <summary>いま開く演出（Width を広げているところ）の最中。</summary>
    private bool _revealing;

    /// <summary>
    /// 開く演出のあいだだけ下げる <c>Window.MinWidth</c>。演出前の値をここへ控えておき、
    /// 演出が終わる・打ち切られるときに戻す。<b>左に寄せているときだけ使う</b>
    /// （<see cref="SlideInLeft"/> 参照）。
    /// </summary>
    private double _minWidthBeforeReveal;

    /// <summary>
    /// <see cref="_minWidthBeforeReveal"/> へ控えてあり、<see cref="EndReveal"/> で
    /// 戻す必要があるか。
    /// <para>
    /// 右に寄せているときは <c>MinWidth</c> をそもそも下げないので、<see cref="EndReveal"/>
    /// は右の演出のあとにまで無条件で <c>MinWidth</c> を書き戻してはいけない
    /// （書き戻すと、下げる前の値を一度も控えていない右の演出のあと 0 に落ちてしまう）。
    /// この旗を見て、左で下げたときだけ戻す。
    /// </para>
    /// </summary>
    private bool _minWidthLowered;

    /// <summary>
    /// 右に寄せているときの開く演出が使う、毎フレームの <c>CompositionTarget.Rendering</c>
    /// ハンドラ。演出中だけ入っており、打ち切り（<see cref="CancelReveal"/>）でも
    /// 必ずここから外す。外し忘れると、引っ込めたあとも毎フレーム呼び続けてしまう。
    /// </summary>
    private EventHandler? _revealRenderingHandler;

    /// <summary>
    /// 右に寄せているときの開く演出で、<c>SetWindowRgn</c> により窓の見える範囲を
    /// 絞っている最中か。
    /// <para>
    /// 打ち切り（<see cref="CancelReveal"/>）で範囲の指定を必ず外すための控え。
    /// これを見ずに外し忘れると、窓が細い帯のまま切り取られて残り、中身が
    /// 見えなくなる（いちばん起きてはいけない壊れ方）。
    /// </para>
    /// </summary>
    private bool _revealClippingRight;

    /// <summary>
    /// <see cref="_revealClippingRight"/> が立っているあいだ、範囲を絞っている窓の
    /// ハンドル。<see cref="CancelReveal"/> から <c>SetWindowRgn(hwnd, IntPtr.Zero, …)</c>
    /// を呼び直すために控えておく。
    /// </summary>
    private IntPtr _revealClipHandle;

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
        _undockReaction = new UndockReaction(shell, WorkAreaGuard.MarkReserved);
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

        _displaySettle = new DispatcherTimer { Interval = DisplaySettle };
        _displaySettle.Tick += (_, _) => RelayoutForDisplay();

        // 画面の構成が変わったら、今の居かたのまま置き場所と見張る帯を決め直す。
        // 起動時に1回合わせただけだと、ログオン直後に構成が落ち着いたあとの画面では
        // 窓が全部の画面の外にあったり、存在しない画面の端を見張り続けたりする
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _window.DpiChanged += OnWindowDpiChanged;

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

        // 全画面アプリなどで外れたら、見た目も合わせる。
        // 終了処理（Dispose）の途中で来たものは、居かたを書き換えない（UndockReaction）
        _appBar.Undocked += (_, _) => _undockReaction.OnUndocked();

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

    /// <summary>
    /// 起動時に、控えてあった居かたへ戻す。
    /// <para>
    /// <b>控えてあった位置が今の画面に乗っているかを、先に確かめる。</b>前日と画面の構成が
    /// 違う（ノート PC を持ち出す・戻す）ことは普通にあり、そのまま使うと窓が全部の画面の外に
    /// 置かれたり、存在しない画面の端を見張ったりする。乗っていなければ、主画面（画面端なら
    /// 同じ端）へ置き直す。乗っていれば何も変えない。判断は <see cref="ShellGeometry"/>。
    /// 保存値・今の画面・置き直したかは <c>shell.log</c> の <c>startup-restore</c> 行に残す。
    /// </para>
    /// </summary>
    public void Restore()
    {
        var mode = _shell.Mode;

        try
        {
            RestoreBeforeApply(mode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 判断に失敗しても、居かたへ戻すこと自体は止めない
            ShellDiagnosticsLog.Write($"startup-restore 判断に失敗した mode={mode} {ex.GetType().Name}: {ex.Message}");
        }

        Apply(mode);

        // 起動の途中（ログオン直後）は、画面の構成がまだ変わることがある。落ち着いたあとで
        // もう一度、今の構成で置き場所と帯を確かめる。同じなら何も動かない
        QueueRelayout("startup");

        var result = CurrentWindowRect();

        ShellDiagnosticsLog.Write(
            $"startup-restore 結果 mode={mode} visible={_window.IsVisible} Left={_window.Left:F1} Top={_window.Top:F1} " +
            $"GetWindowRect={(result is { } rect ? ShellGeometry.FormatRect(rect) : "取得不可")}");
    }

    /// <summary>
    /// <see cref="Restore"/> の下ごしらえ。窓の矩形と今の画面から、置き直しが要るか決めて記録する。
    /// </summary>
    private void RestoreBeforeApply(ShellMode mode)
    {
        var scale = Scale();
        var screens = Screens.All(scale);
        var current = CurrentWindowRect();

        _lastScreens = screens;

        if (mode == ShellMode.Window)
        {
            // 最大化・最小化の窓は、Windows が置き場所を決める。矩形を触らない
            if (current is not { } saved || _window.WindowState != WindowState.Normal)
            {
                ShellDiagnosticsLog.Write(
                    $"startup-restore mode=Window saved={(current is { } r ? ShellGeometry.FormatRect(r) : "取得不可")} " +
                    $"state={_window.WindowState} screens={ShellGeometry.DescribeScreens(screens)} → 触らない");
                return;
            }

            var fit = ShellGeometry.RestoreWindow(saved, screens);

            ShellDiagnosticsLog.Write(ShellGeometry.FormatWindowRestoreLine(saved, screens, fit));

            if (fit.Relocated) PlaceWindow(fit.Placed);

            return;
        }

        var decision = ShellGeometry.DecideEdge(current, _shell.Edge, _shell.DockWidth, screens);

        ShellDiagnosticsLog.Write(ShellGeometry.FormatEdgeRestoreLine(
            mode, _shell.Edge, _shell.DockWidth, current, screens, decision));

        // 窓が今の画面のどこにも乗っていなければ、先に主画面の端へ寄せておく。
        // このあとの Apply は「窓が乗っている画面」を基準に置くので、乗せておかないと
        // 画面外の窓から最も近い画面を引くことになり、見張る帯とずれる
        if (decision.Relocated && decision.ScreenIndex >= 0) PlaceWindow(decision.Target);
    }

    /// <summary>いまの窓の矩形（物理ピクセル）。ハンドルが無い・取れなければ <c>null</c>。</summary>
    private NativeMethods.RECT? CurrentWindowRect()
    {
        var handle = new WindowInteropHelper(_window).Handle;

        return handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var rect) ? rect : null;
    }

    /// <summary>
    /// 窓を物理ピクセルの矩形へ置く。1 DIP 未満のずれでは代入しない。
    /// </summary>
    private void PlaceWindow(NativeMethods.RECT rect)
    {
        var scale = Scale();

        void Set(DependencyProperty property, double value, double current)
        {
            if (ShellGeometry.ShouldMove(value, current)) _window.SetValue(property, value);
        }

        Set(Window.LeftProperty, rect.left / scale, _window.Left);
        Set(Window.TopProperty, rect.top / scale, _window.Top);
        Set(Window.WidthProperty, rect.Width / scale, _window.Width);
        Set(Window.HeightProperty, rect.Height / scale, _window.Height);
    }

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
    /// <b>窓は最初から定位置（寄せている辺）にあり、それを広げて「めくれるように」
    /// 見せる。</b>画面の外から <c>Left</c> を動かして滑り込ませていた前の作りは、
    /// 窓の右端（寄せている辺と逆側）が先に画面へ入ってしまい、いちばん先に読みたい
    /// 左端の中身が最後に到着するうえ、中身が横に流れて落ち着かなかった。
    /// </para>
    /// <para>
    /// <b>左右で作りが違う。</b> Windows は、ウィンドウを左の辺から広げると、
    /// 描き直しが追い付くまでのあいだ古い中身を窓の左上に合わせて表示する。左に
    /// 寄せた窓は右へ広がるので、この性質があっても古い中身がそのまま正しい場所に
    /// 残り、問題が起きない――だから左のときは <see cref="SlideInLeft"/> で、
    /// これまでどおり <c>Window.Width</c> を WPF の <c>DoubleAnimation</c> で広げる。
    /// 右に寄せた窓は逆に左へ広がるので、同じやり方だと中身が壁から離れて左へ
    /// 引きずられるように見えてしまう（実機の記録で確認済み。<c>SetWindowPos</c>
    /// を1回にまとめても、窓の大きさを変えていること自体が原因なので直らなかった）。
    /// だから右のときだけ <see cref="SlideInRight"/> に任せ、<b>窓の大きさは
    /// 一切変えず</b> <c>SetWindowRgn</c> で見える範囲だけを広げる。
    /// <b>次に誰かが「左右で揃えよう」としても、この理由でまた壊れるので揃えないこと。</b>
    /// </para>
    /// <para>
    /// 中身（<see cref="ISlideRevealHost"/>）の幅は、どちらの辺でも定位置に固定して
    /// 端へ寄せておく。左のときは窓の <c>Width</c> が動くのでこれが要る。右のときは
    /// 窓がはじめから定位置の幅なので害は無い。
    /// </para>
    /// <para>位置を決めてから出す。出してから動かすと、一度別の場所に見えて飛ぶ。</para>
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

        ApplyOverlayBounds(verify: true);

        // 定位置（このあと動かさない値）を、左の演出が Width を縮める前に確定しておく
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

        if (_shell.Edge == DockEdge.Right) SlideInRight();
        else SlideInLeft(restingLeft, restingWidth);

        // 出たあとは、外れるのを見張る番
        if (SlideOutOnLeave) _hotZone.WatchLeaving(shown);
    }

    /// <summary>
    /// 左に寄せているときの開く演出。窓を <see cref="SlideRevealStartWidth"/> まで
    /// いったん畳んでから、<c>Window.Width</c> を <see cref="AnimateReveal"/> で
    /// 広げる。従来どおりの作り（詳しくは <see cref="SlideIn"/> のコメント）。
    /// </summary>
    private void SlideInLeft(double restingLeft, double restingWidth)
    {
        // 窓をいったん畳んでおく。0 だと WPF が嫌がる場面があるので 1 にする。
        // MinWidth がそのままだとそこで頭打ちになるので、演出のあいだだけ下げる
        _minWidthBeforeReveal = _window.MinWidth;
        _minWidthLowered = true;
        _window.MinWidth = SlideRevealStartWidth;
        _window.Width = SlideRevealStartWidth;

        // 左に寄せているときは Left はもう動かさない（ShellGeometry.RevealLeft）
        _window.Left = ShellGeometry.RevealLeft(_shell.Edge, restingLeft, restingWidth, SlideRevealStartWidth);

        Show();
        LogWindowRect("Show直後");

        // Show() の直後、同じフレームでアニメーションを始めない。窓が出る処理と
        // 競合して開始値が拾われないことがあるため、出きった次のフレームまで待つ
        // （実機で確認済み。引っ込み側 SlideOutIfIdle はこの競合が無く、完璧に動いている）
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
            AnimateReveal(startWidth, restingWidth,
                () =>
                {
                    _revealHost?.EndSlideReveal();

                    // 滑り出しているあいだに他のアプリが前面を取ると、出きった時点で
                    // 下に潜っていることがある。終わりにもう一度入れ直す
                    RaiseToTopIfOverlay();

                    LogWindowRect("Animate完了");
                });
        }));
    }

    /// <summary>
    /// 右に寄せているときの開く演出。
    /// <para>
    /// <b>窓の大きさ・位置は最初から定位置（<see cref="ApplyOverlayBounds"/> が
    /// 置いた値）のまま一切変えない。</b>広げているように見せる仕事は、窓の
    /// <c>Width</c> ではなく <c>SetWindowRgn</c> による「見える範囲」の切り取りに
    /// 任せる。中身は常に定位置に描かれているので、見える範囲を右端から左へ
    /// 広げていっても、中身が動いて見えることはない（なぜ右だけこの作りにしたかは
    /// <see cref="SlideIn"/> のコメントを参照）。
    /// </para>
    /// <para>
    /// 座標は窓の左上を原点とする物理ピクセル。<c>WindowStyle=None</c> なので枠が無く、
    /// 窓の矩形そのものが基準になる。<see cref="Scale"/> で DIP から物理ピクセルへ
    /// 換算する。
    /// </para>
    /// <para>
    /// <b>一度も出したことが無い窓はまだハンドルが無い。</b> 見せる前に範囲を絞る
    /// 必要があるので、<c>WindowInteropHelper.EnsureHandle()</c> で先にハンドルを
    /// 作らせる。絞っておかないと、定位置・全幅の窓が一瞬まるごと見えてしまう。
    /// </para>
    /// <para>
    /// <b>打ち切られても、<see cref="CancelReveal"/> が必ず範囲の指定を外す。</b>
    /// 演出中かどうかを <see cref="_revealClippingRight"/> に、窓のハンドルを
    /// <see cref="_revealClipHandle"/> に控えておき、<see cref="CancelReveal"/> が
    /// そこを見て外す。外し忘れると、窓が細い帯のまま切り取られて残り、中身が
    /// 見えなくなる（いちばん起きてはいけない壊れ方）。
    /// </para>
    /// </summary>
    private void SlideInRight()
    {
        var handle = new WindowInteropHelper(_window).EnsureHandle();

        var scale = Scale();
        var widthPhysical = Math.Max(1, (int)Math.Round(_window.Width * scale));
        var heightPhysical = Math.Max(1, (int)Math.Round(_window.Height * scale));

        ShellDiagnosticsLog.Write(
            $"SlideIn 開く演出 edge=Right 方式=SetWindowRgn restingLeft={_window.Left:F1} " +
            $"restingWidth={_window.Width:F1} widthPhysical={widthPhysical} heightPhysical={heightPhysical}");

        // 見せる前に、見える範囲を右端 1px 幅の帯にしておく
        SetRevealRegion(handle, widthPhysical, heightPhysical, 1);
        _revealClippingRight = true;
        _revealClipHandle = handle;

        Show();
        LogWindowRect("Show直後");
        LogWindowRect("アニメーション開始直前");

        // 前の演出の処理が残っていれば外してから登録する。二重に走ると、
        // 2本の処理が同じ窓の範囲を奪い合う
        if (_revealRenderingHandler is { } previous)
        {
            System.Windows.Media.CompositionTarget.Rendering -= previous;
            _revealRenderingHandler = null;
        }

        var totalMs = SlideInTime.TimeSpan.TotalMilliseconds;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var frame = 0;

        void OnRendering(object? sender, EventArgs e)
        {
            var t = totalMs <= 0 ? 1.0 : Math.Min(1.0, stopwatch.Elapsed.TotalMilliseconds / totalMs);
            var eased = SlideRevealEasing.Ease(t);
            var revealPhysical = Math.Clamp((int)Math.Round(widthPhysical * eased), 1, widthPhysical);

            SetRevealRegion(handle, widthPhysical, heightPhysical, revealPhysical);

            // 毎コマは記録しない。何かあったとき記録だけで切り分けられるよう、
            // 何コマかに1回だけ見える幅を残す
            frame++;
            if (t >= 1.0 || frame % 4 == 0)
            {
                // コマごとの行は間引く（1秒に1行と、止まったときの最後の値）
                ShellDiagnosticsLog.WriteThrottled(
                    "SlideIn 開く演出(右)",
                    $"SlideIn 開く演出(右) t={t:F2} revealPhysical={revealPhysical}/{widthPhysical}");
            }

            if (t < 1.0) return;

            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            _revealRenderingHandler = null;

            // 範囲の指定を外す（窓全体が見える元の状態に戻す）
            ClearRevealRegion(handle);
            _revealClippingRight = false;
            _revealClipHandle = IntPtr.Zero;

            EndReveal();
            _revealHost?.EndSlideReveal();

            // 滑り出しているあいだに他のアプリが前面を取ると、出きった時点で
            // 下に潜っていることがある。終わりにもう一度入れ直す
            RaiseToTopIfOverlay();

            LogWindowRect("Animate完了");
        }

        _revealRenderingHandler = OnRendering;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// 窓の見える範囲を、右端から <paramref name="revealPhysical"/> px ぶんの帯に絞る。
    /// <para>
    /// <c>SetWindowRgn</c> に渡したリージョンのハンドルは、成功したら OS が持ち主に
    /// なるので自分で <c>DeleteObject</c> しない。失敗（0 が返る）したときだけ、
    /// 自分で作ったリージョンを片付ける。
    /// </para>
    /// </summary>
    private static void SetRevealRegion(IntPtr handle, int widthPhysical, int heightPhysical, int revealPhysical)
    {
        var region = NativeMethods.CreateRectRgn(
            widthPhysical - revealPhysical, 0, widthPhysical, heightPhysical);

        if (NativeMethods.SetWindowRgn(handle, region, true) == 0)
        {
            NativeMethods.DeleteObject(region);
        }
    }

    /// <summary>範囲の指定を外し、窓全体が見える元の状態に戻す。</summary>
    private static void ClearRevealRegion(IntPtr handle) =>
        NativeMethods.SetWindowRgn(handle, IntPtr.Zero, true);

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

        if (_slidingOut) return;

        // 窓が見えていない（トレイへ閉じた・隠れた）。外れるのを見張っても引っ込める
        // ものが無いので、帯の見張りへ戻す。戻さないと、窓を出し直すまで帯が反応せず、
        // 「端にマウスを当てても出てこない」ままになる
        if (!_window.IsVisible)
        {
            NoteSlideOutSkip("窓が見えていないので、帯の見張りへ戻した");
            _hotZone.WatchEdge();
            return;
        }

        // 幅をつまんでいる最中。手が窓の外に出ていても引っ込めない
        if (_shell.IsResizing)
        {
            NoteSlideOutSkip("幅をつまんでいる");
            return;
        }

        // 自分が出したメニュー・ポップアップが開いている最中。狭いスライドでは
        // メニューが窓の右端をはみ出し、その上へカーソルを動かすとホットゾーンが
        // 「窓から外れた」と判定していた。開いているあいだは引っ込めない（項目3）
        if (!ignorePopups && PopupActivityHooks.Tracker.IsAnyOpen)
        {
            NoteSlideOutSkip("メニュー・ポップアップが開いている");
            return;
        }

        // 自分が出した窓（編集画面など）に移っただけなら、引っ込めない。
        // 予定を書いている最中に本体が消えると、書き終わって戻る先が無くなる
        if (OwnsForeground())
        {
            NoteSlideOutSkip("自分の別のウィンドウが前面にある");
            return;
        }

        _lastSlideOutSkip = null;

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
    /// 引っ込めるのを見送った理由を <c>shell.log</c> へ残す。
    /// <para>
    /// 見送ると、窓は出たままになる。<c>shell.log</c> に <c>OffScreenLeft</c> が出ない起動が
    /// あったとき、見送られていたのか、そもそも判定が走っていなかったのかを分けられる。
    /// 外れているあいだは約 0.5 秒ごとに来るので、理由が変わったときだけ書く。
    /// </para>
    /// </summary>
    private void NoteSlideOutSkip(string reason)
    {
        if (string.Equals(_lastSlideOutSkip, reason, StringComparison.Ordinal)) return;

        _lastSlideOutSkip = reason;

        ShellDiagnosticsLog.Write($"SlideOut 見送り 理由={reason} visible={_window.IsVisible} edge={_shell.Edge}");
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
                $"startup virtualScreen=(left={SystemParameters.VirtualScreenLeft:F0}," +
                $"width={SystemParameters.VirtualScreenWidth:F0}) primaryScreenWidth=" +
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
    /// 幅を「めくれるように」広げる（開く演出）。<b>左に寄せているときだけ使う。</b>
    /// <para>
    /// 中身は <see cref="ISlideRevealHost.BeginSlideReveal"/> で固定してあるので、
    /// ここは窓の <c>Width</c> を WPF の <c>DoubleAnimation</c> で動かすだけでよい。
    /// 右に寄せているときは話がまるで別で、<see cref="SlideInRight"/> が
    /// <c>SetWindowRgn</c> で見える範囲を広げる作りを使う（<see cref="SlideIn"/> の
    /// コメントに理由がある）。
    /// </para>
    /// </summary>
    private void AnimateReveal(double fromWidth, double toWidth, Action? done)
    {
        ShellDiagnosticsLog.Write(
            $"SlideIn 開く演出 edge={_shell.Edge} widthFrom={fromWidth:F1} widthTo={toWidth:F1} " +
            $"dockWidth={_shell.DockWidth:F1}");

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
    /// 開く演出を打ち切る。演出中でなければ何もしない。
    /// <para>
    /// <see cref="SlideOutIfIdle"/> 自体は変えない（引っ込みは完璧に動いている）。
    /// 呼び出し側でここを先に呼ぶことで、<see cref="SlideInTime"/>（220ms）より
    /// 短い間隔で引っ込みが割り込んでも、広がる演出と画面外へ動く演出が重ならない
    /// ようにする。
    /// </para>
    /// <para>
    /// <c>StopSliding</c> など複数の経路から呼ばれる一本道なので、右に寄せていると
    /// きの後始末（<c>SetWindowRgn</c> の範囲を外す）もここへ集約してある。
    /// </para>
    /// </summary>
    private void CancelReveal()
    {
        if (!_revealing) return;

        EndReveal();
        _window.BeginAnimation(Window.WidthProperty, null);
        _window.BeginAnimation(Window.LeftProperty, null);

        // 右に寄せているときの開く演出は WPF のアニメーションではなく
        // CompositionTarget.Rendering で毎コマ SetWindowRgn を呼んでいる。
        // 外し忘れると、引っ込めたあとも毎フレーム呼び続けてしまう
        if (_revealRenderingHandler is { } handler)
        {
            System.Windows.Media.CompositionTarget.Rendering -= handler;
            _revealRenderingHandler = null;
        }

        // 範囲の指定を必ず外す。外し忘れると、窓が細い帯のまま切り取られて残り、
        // 中身が見えなくなる（いちばん起きてはいけない壊れ方）。演出中かどうかと
        // 窓のハンドルはフィールドに控えてあるので、呼び出し経路を問わずここで外れる
        if (_revealClippingRight)
        {
            ClearRevealRegion(_revealClipHandle);
            _revealClippingRight = false;
            _revealClipHandle = IntPtr.Zero;
        }

        _revealHost?.EndSlideReveal();
    }

    /// <summary>
    /// 演出中フラグを下ろし、左の演出向けに下げていた <c>MinWidth</c> を元に戻す。
    /// <para>
    /// 右の演出は <c>MinWidth</c> を下げていないので、<see cref="_minWidthLowered"/>
    /// が立っているとき（左の演出のとき）だけ戻す。無条件に戻すと、右の演出のあと
    /// 一度も控えていない値（0）へ落としてしまう。
    /// </para>
    /// </summary>
    private void EndReveal()
    {
        _revealing = false;

        if (!_minWidthLowered) return;

        _window.MinWidth = _minWidthBeforeReveal;
        _minWidthLowered = false;
    }

    /// <summary>滑りを止めて、位置を自分の手に戻す。</summary>
    private void StopSliding()
    {
        _slidingOut = false;
        _window.BeginAnimation(Window.LeftProperty, null);

        // 開く演出（左は Width のアニメーション、右は SetWindowRgn の範囲）が
        // 残っていたら、ここで打ち切る。Width のアニメーションを掛けたままだと、
        // このあと Width へ入れる値が効かなくなる
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
        _displaySettle.Stop();

        // static なイベントなので、外さないとこのオブジェクトを握ったままになる
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _window.DpiChanged -= OnWindowDpiChanged;

        // 削ったまま終わらせない。ここを通らずに落ちた場合は、
        // 次の起動で WorkAreaGuard が戻す。
        //
        // 外したときの Undocked で居かたを Overlay に書き換えてはいけない（次の起動で
        // ピンが外れる）。その書き換えが消していた終了印は、Shutdown が明示的に消す
        _undockReaction.Shutdown(_appBar.Dispose);
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
                ToEdge(ShellMode.Overlay);
                ApplyOverlayBounds(verify: true);

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

                // タスクバーのボタンを外すのは、AppBar を登録する前（ToEdge の中）。
                // 登録したあとに窓の見せ方を変えると、窓のハンドルに掛けたもの
                // （AppBar の登録・メッセージのフック）に響くおそれがある
                ToEdge(ShellMode.Dock);

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

        // AppBar は Apply が先に外してある（登録中に触らない）
        SetTaskbarPresence(ShellMode.Window);

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

        // 端へ寄せているあいだに画面の構成が変わると、寄せる前の位置が今の画面に
        // 無いことがある。そのまま戻すと窓が画面の外に出る。乗っていなければ収める
        FitWindowToScreens("ウィンドウへ戻した");
    }

    /// <summary>
    /// 端へ寄せる形にする。
    /// <para>
    /// 枠を消すのは、画面端に貼り付いたときにタイトルバーが場所を食うから
    /// （要件書 7.2）。戻せるよう、寄せる前の姿を控えておく。
    /// </para>
    /// </summary>
    private void ToEdge(ShellMode mode)
    {
        StopSliding();

        // 見せる前・AppBar を登録する前に決める。起動時の復元もここを通る
        SetTaskbarPresence(mode);

        _windowed ??= new WindowPlacement(
            _window.Left, _window.Top, _window.Width, _window.Height, IsMaximized: false);

        if (_window.WindowState == WindowState.Maximized) _window.WindowState = WindowState.Normal;

        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.Topmost = true;
    }

    /// <summary>
    /// 居かたに応じて、タスクバーのボタンを出す・出さないを切り替える。
    /// <para>
    /// <b>呼ぶ順序が肝。</b>表示中の窓の <c>ShowInTaskbar</c> を変えると、WPF は
    /// 窓の拡張スタイルを書き換え、ボタンを更新するために窓を隠して出し直すことがある。
    /// 窓のハンドルに掛けているもの（AppBar の登録・メッセージのフック・
    /// <c>SetWindowRgn</c> の範囲）が巻き添えにならないよう、AppBar を外したあと・
    /// 登録する前、開く演出を打ち切ったあとにだけ呼ぶ（<see cref="Apply"/> の並びと
    /// <see cref="ToWindow"/>・<see cref="ToEdge"/> がそれを守っている）。
    /// </para>
    /// <para>
    /// 窓のハンドルが作り直されると、ホットキー（<c>GlobalHotKeys</c>）と AppBar の
    /// フックが外れる。作り直されていないかは、<c>shell.log</c> の前後のハンドルで見られる。
    /// </para>
    /// </summary>
    private void SetTaskbarPresence(ShellMode mode)
    {
        var show = ShellGeometry.ShowsInTaskbar(mode);
        if (_window.ShowInTaskbar == show) return;

        var before = new WindowInteropHelper(_window).Handle;

        _window.ShowInTaskbar = show;

        var after = new WindowInteropHelper(_window).Handle;

        ShellDiagnosticsLog.Write(
            $"ShowInTaskbar mode={mode} -> {show} hwnd=0x{before.ToInt64():X}->0x{after.ToInt64():X}" +
            (before != after ? " ハンドルが作り直された" : string.Empty));
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
    /// <para>
    /// どの画面の端かは、窓が乗っている画面で決める。どの画面にも乗っていなければ主画面
    /// （<see cref="ShellGeometry.DecideEdge"/>）。見張る帯も同じ画面で張るので、窓と帯が別の
    /// 画面を指すことが無い。
    /// </para>
    /// </summary>
    /// <param name="verify">
    /// 置いたあとに実際の矩形を確かめ、倍率の切り替わりでずれていれば置き直す。幅をつまんで
    /// いるあいだは何度も呼ばれるので、そこでは確かめない。
    /// </param>
    private void ApplyOverlayBounds(bool verify = false)
    {
        if (!_shell.IsAtEdge || _shell.IsPinned) return;

        // 滑りが残っていると、ここで入れた値が効かない。アニメーションは
        // 掛けたあいだ値を握り続ける
        StopSliding();

        // 留める前に控えた値があればそちらを使い、使ったら捨てる。次に出すときには
        // ワークエリアも戻っているので、そのときは素直に測ってよい
        var measured = _workBeforeDock is null;
        var work = _workBeforeDock ?? DockWorkArea();
        _workBeforeDock = null;

        SetOverlayBounds(work);

        // 倍率の違う画面へ移すと、代入の途中で Windows が倍率を切り替え、大きさや位置が
        // ずれることがある（WM_DPICHANGED）。実際の矩形を見て、ずれていれば切り替わった
        // あとの倍率でもう1回だけ置く。ピン留めを外した直後の値（控えたもの）は
        // 測り直せないので、この確認はしない
        if (verify && measured && !OverlayBoundsApplied())
        {
            ShellDiagnosticsLog.Write(
                $"OverlayBounds 置いた矩形が狙いとずれた→倍率が切り替わったとみて置き直す " +
                $"GetWindowRect={FormatCurrentRect()} scale={Scale():F2}");

            SetOverlayBounds(DockWorkArea());
        }

        // 幅を変えたら、見張る範囲も合わせる。
        //
        // 出したときの矩形のまま見張っていると、掴んで広げた先にカーソルを
        // 置いた時点で「窓から外れた」ことになって引っ込む。つまり、
        // 幅を広げようとすると必ず消える――幅を変えられない、という形で出る
        if (_window.IsVisible && SlideOutOnLeave) _hotZone.WatchLeaving(WindowRectAt(_window.Left));
    }

    /// <summary>作業領域（DIP）の寄せている辺に、窓を合わせる。</summary>
    private void SetOverlayBounds(Rect work)
    {
        var width = _shell.DockWidth;

        _window.Top = work.Top;
        _window.Height = work.Height;
        _window.Width = width;
        _window.Left = _shell.Edge == DockEdge.Left ? work.Left : work.Right - width;
    }

    /// <summary>
    /// 窓が、いま寄せるはずの画面の端に、狙いどおり置かれているか。
    /// 矩形が取れないときは、確かめようがないので true（余計な置き直しをしない）。
    /// </summary>
    private bool OverlayBoundsApplied()
    {
        if (CurrentWindowRect() is not { } actual) return true;

        var scale = Scale();
        var screens = Screens.All(scale);
        var decision = ShellGeometry.DecideEdge(actual, _shell.Edge, _shell.DockWidth, screens);

        if (decision.ScreenIndex < 0) return true;

        return ShellGeometry.Same(actual, decision.Target, (int)Math.Ceiling(scale) + 1);
    }

    private string FormatCurrentRect() =>
        CurrentWindowRect() is { } rect ? ShellGeometry.FormatRect(rect) : "取得不可";

    /// <summary>
    /// 画面端（スライド）を置く作業領域（DIP）。窓が乗っている画面、無ければ主画面。
    /// 画面の一覧が取れなければ、従来どおり窓の最寄りのモニタ（<see cref="CurrentMonitorWorkArea"/>）。
    /// </summary>
    private Rect DockWorkArea()
    {
        var scale = Scale();
        var screens = Screens.All(scale);
        var decision = ShellGeometry.DecideEdge(CurrentWindowRect(), _shell.Edge, _shell.DockWidth, screens);

        if (decision.ScreenIndex < 0) return CurrentMonitorWorkArea();

        var work = screens[decision.ScreenIndex].Work;

        return new Rect(work.left / scale, work.top / scale, work.Width / scale, work.Height / scale);
    }

    // ------------------------------------------------------------------
    // 画面の構成が変わったとき
    // ------------------------------------------------------------------

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // この知らせは UI のスレッドには来ない。渡し直してから触る
        if (_disposed) return;

        _window.Dispatcher.BeginInvoke(() => QueueRelayout("DisplaySettingsChanged"));
    }

    private void OnWindowDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        // 自分の置き直しが起こした倍率の変更は数えない（置き直しの中で確かめている）
        if (_relayouting) return;

        QueueRelayout("DpiChanged");
    }

    /// <summary>
    /// 置き直しを頼む。続けて来るので、来なくなってから1回だけ走らせる。
    /// </summary>
    private void QueueRelayout(string cause)
    {
        if (_disposed) return;

        _displayCauses.Add(cause);
        _displayDeferrals = 0;
        _displaySettle.Stop();
        _displaySettle.Start();
    }

    /// <summary>
    /// 画面の構成が変わったので、今の居かたのまま置き場所と見張る帯を決め直す。
    /// <para>
    /// ウィンドウ：どの画面にも手が届かなければ主画面へ収める。スライド：今の画面の端へ置き直し、
    /// 帯の見張りを張り直す（存在しなくなった画面の端を見張り続けない）。ピン留め：AppBar の
    /// 位置を交渉し直す（<c>WM_DISPLAYCHANGE</c> では <see cref="AppBarHost"/> も即座に動くが、
    /// 作業領域が追い付いたあとで確かめ直す。同じ矩形なら何もしない）。
    /// </para>
    /// <para>
    /// 開く演出・引っ込め・幅つまみの最中は、それが済むまで待つ（演出を打ち切ると
    /// <c>SetWindowRgn</c> の範囲が残るなどの壊れ方をする）。
    /// </para>
    /// </summary>
    private void RelayoutForDisplay()
    {
        _displaySettle.Stop();

        if (_disposed) return;

        if ((_revealing || _slidingOut || _shell.IsResizing || _resizeSettle.IsEnabled)
            && _displayDeferrals < MaxDisplayDeferrals)
        {
            _displayDeferrals++;
            _displaySettle.Start();
            return;
        }

        var cause = string.Join("+", _displayCauses.Distinct());
        _displayCauses.Clear();

        var gaveUp = _displayDeferrals >= MaxDisplayDeferrals;
        _displayDeferrals = 0;

        _relayouting = true;

        try
        {
            var scale = Scale();
            var screens = Screens.All(scale);
            var layoutChanged = _lastScreens is null || !ShellGeometry.SameLayout(_lastScreens, screens);

            _lastScreens = screens;

            var head =
                $"display-change cause={cause} mode={_shell.Mode} edge={_shell.Edge} " +
                $"構成変化={(layoutChanged ? "あり" : "なし")} screens={ShellGeometry.DescribeScreens(screens)}" +
                (gaveUp ? " (演出などが終わらないまま決め直した)" : string.Empty);

            switch (_shell.Mode)
            {
                case ShellMode.Window:
                    RelayoutWindow(head, screens);
                    break;

                case ShellMode.Overlay:
                    RelayoutOverlay(head);
                    break;

                case ShellMode.Dock:
                    ShellDiagnosticsLog.Write($"{head} → ピン留めの位置を交渉し直す（同じなら何もしない）");
                    _appBar.Resize(_shell.DockWidth);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 置き直しに失敗しても、いまの姿のまま動き続ける
            ShellDiagnosticsLog.Write($"display-change 置き直しに失敗した cause={cause} {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _relayouting = false;
        }
    }

    /// <summary>ウィンドウのとき：どの画面にも手が届かなければ、主画面へ収める。</summary>
    private void RelayoutWindow(string head, IReadOnlyList<ScreenInfo> screens)
    {
        if (CurrentWindowRect() is not { } rect || _window.WindowState != WindowState.Normal)
        {
            ShellDiagnosticsLog.Write($"{head} → 最大化・最小化中か矩形が取れないので触らない state={_window.WindowState}");
            return;
        }

        var fit = ShellGeometry.RestoreWindow(rect, screens);

        if (!fit.Relocated)
        {
            ShellDiagnosticsLog.Write($"{head} → 窓はそのまま window={ShellGeometry.FormatRect(rect)} 理由={fit.Reason}");
            return;
        }

        PlaceWindow(fit.Placed);

        ShellDiagnosticsLog.Write(
            $"{head} → 窓を置き直した {ShellGeometry.FormatRect(rect)} → {ShellGeometry.FormatRect(fit.Placed)} 理由={fit.Reason}");
    }

    /// <summary>
    /// スライドのとき：窓の置き場所と、帯の見張りを決め直す。
    /// 出ているなら、外れるのを見張る範囲も合わせる（<see cref="ApplyOverlayBounds"/>）。
    /// </summary>
    private void RelayoutOverlay(string head)
    {
        if (!_shell.IsAtEdge || _shell.IsPinned) return;

        var before = CurrentWindowRect();

        ApplyOverlayBounds(verify: true);

        // 帯の見張りを、いま窓が乗っている画面で張り直す。出ているあいだは、
        // 外れるのを見張る状態へ戻す（Apply(Overlay) と同じ）
        var screen = ScreenOfWindow();
        _hotZone.Arm(_shell.Edge, screen);

        if (_window.IsVisible && SlideOutOnLeave) _hotZone.WatchLeaving(WindowRectAt(_window.Left));

        var after = CurrentWindowRect();
        var moved = before is { } b && after is { } a ? !ShellGeometry.Same(a, b, 0) : true;

        ShellDiagnosticsLog.Write(
            $"{head} → 置き場所と見張る帯を決め直した " +
            $"screen=({screen.left},{screen.top},{screen.right},{screen.bottom}) " +
            $"window={(after is { } rect ? ShellGeometry.FormatRect(rect) : "取得不可")} " +
            $"動いた={(moved ? "はい" : "いいえ")} visible={_window.IsVisible}");
    }

    /// <summary>
    /// 普通の窓を、手が届く位置へ収める。端へ寄せる前の姿へ戻した直後に使う。
    /// </summary>
    private void FitWindowToScreens(string why)
    {
        if (_window.WindowState != WindowState.Normal) return;

        if (CurrentWindowRect() is not { } rect) return;

        var screens = Screens.All(Scale());
        var fit = ShellGeometry.RestoreWindow(rect, screens);

        if (!fit.Relocated) return;

        PlaceWindow(fit.Placed);

        ShellDiagnosticsLog.Write(
            $"window-fit {why} {ShellGeometry.FormatRect(rect)} → {ShellGeometry.FormatRect(fit.Placed)} 理由={fit.Reason}");
    }
}
