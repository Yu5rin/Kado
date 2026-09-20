using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Shell;

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

    private readonly Window _window;
    private readonly ShellViewModel _shell;
    private readonly DockPlacementStore _store;
    private readonly AppBarHost _appBar;
    private readonly EdgeHotZone _hotZone;
    private readonly DispatcherTimer _resizeSettle;

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

        // 幅をつまみ終えたら、見張る範囲を今の姿に合わせ直す
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ShellViewModel.IsResizing)) return;
            if (_shell.IsResizing || !SlideOutOnLeave) return;
            if (_shell.Mode != ShellMode.Overlay || !_window.IsVisible) return;

            _hotZone.WatchLeaving(WindowRectAt(_window.Left));
        };

        // 帯に留まったらスライドさせる
        _hotZone.Triggered += (_, _) => SlideIn();

        // 窓から外れたら引っ込める。押そうとしたボタンが逃げないよう、外れてから
        // 少し置いてから来る
        _hotZone.Left += (_, _) => SlideOutIfIdle();

        // 他のアプリへ移ったら引っ込める。スライドは「用があるときだけ出る」もので、
        // 出しっぱなしにしたいならピンで留める
        _window.Deactivated += (_, _) => SlideOutIfIdle();

        // 全画面アプリなどで外れたら、見た目も合わせる
        _appBar.Undocked += (_, _) =>
        {
            if (_shell.IsPinned) _shell.Mode = ShellMode.Overlay;
        };
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
    }

    /// <summary>
    /// スライドさせて出す。
    /// <para>位置を決めてから出す。出してから動かすと、一度別の場所に見えて飛ぶ。</para>
    /// </summary>
    private void SlideIn()
    {
        if (_shell.Mode != ShellMode.Overlay) return;

        ApplyOverlayBounds();

        var resting = _window.Left;

        // 見張る矩形は「落ち着いたあとの居場所」で取る。
        // いまの Left はこれから画面の外へ動かす値なので、そのまま渡すと
        // 画面外の矩形を見張ることになり、カーソルがどこにあっても「外れている」
        // と判定されて、出たそばから引っ込む
        var shown = WindowRectAt(resting);

        // 画面の外から滑り込ませる。位置を決めてから出す
        _window.Left = OffScreenLeft();
        Show();

        Animate(resting, SlideInTime, new QuinticEase { EasingMode = EasingMode.EaseOut });

        // 出たあとは、外れるのを見張る番
        if (SlideOutOnLeave) _hotZone.WatchLeaving(shown);
    }

    /// <summary>
    /// 引っ込める。
    /// <para>
    /// スライド中だけ。ピンで留めているあいだは、他のアプリへ移っても出したままにする。
    /// </para>
    /// </summary>
    private void SlideOutIfIdle()
    {
        if (_shell.Mode != ShellMode.Overlay) return;

        if (_slidingOut || !_window.IsVisible) return;

        // 幅をつまんでいる最中。手が窓の外に出ていても引っ込めない
        if (_shell.IsResizing) return;

        // 自分が出した窓（編集画面など）に移っただけなら、引っ込めない。
        // 予定を書いている最中に本体が消えると、書き終わって戻る先が無くなる
        if (OwnsForeground()) return;

        var resting = _window.Left;
        _slidingOut = true;

        Animate(OffScreenLeft(), SlideOutTime, new QuadraticEase { EasingMode = EasingMode.EaseIn },
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
    /// <para>寄せている辺の向こう側へ、まるごと1枚ぶん出す。</para>
    /// </summary>
    private double OffScreenLeft()
    {
        var scale = Scale();
        var screen = ScreenOfWindow();
        var width = _window.Width;

        return _shell.Edge == DockEdge.Left
            ? (screen.left / scale) - width
            : screen.right / scale;
    }

    /// <summary>
    /// 横に滑らせる。
    /// <para>
    /// <b>終わったらアニメーションを外す。</b>掛けたままだと、そのあと
    /// <c>Left</c> に入れた値が効かなくなる（アニメーションが値を握り続ける）。
    /// </para>
    /// </summary>
    private void Animate(double to, Duration time, IEasingFunction easing, Action? done = null)
    {
        var animation = new DoubleAnimation(to, time) { EasingFunction = easing };

        animation.Completed += (_, _) =>
        {
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.Left = to;
            done?.Invoke();
        };

        _window.BeginAnimation(Window.LeftProperty, animation);
    }

    /// <summary>滑りを止めて、位置を自分の手に戻す。</summary>
    private void StopSliding()
    {
        _slidingOut = false;
        _window.BeginAnimation(Window.LeftProperty, null);
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

    /// <summary>いま前にいるのが、自分の出した窓か。</summary>
    private bool OwnsForeground() =>
        Application.Current?.Windows.OfType<Window>()
            .Any(w => !ReferenceEquals(w, _window) && w.IsActive) ?? false;

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
                break;

            case ShellMode.Dock:
                _hotZone.Disarm();

                // 削る前に控える。外したあとでは正しい値が取れない
                _workBeforeDock ??= SystemParameters.WorkArea;

                ToEdge();

                // ドックのあいだは最前面にしない。場所を譲ってもらっているので、
                // 重ねる必要がない。立てたままだと他のアプリの邪魔になる
                _window.Topmost = false;

                if (_appBar.Dock(_shell.Edge, _shell.DockWidth)) break;

                // 削れなかった。黙って重なったままにせず、理由を伝えてから落とす
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
        var work = _workBeforeDock ?? SystemParameters.WorkArea;
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
