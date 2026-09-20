using System.Windows;
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

        // 帯に留まったらスライドさせる
        _hotZone.Triggered += (_, _) => SlideIn();

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
        Show();
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

        // 自分が出した窓（編集画面など）に移っただけなら、引っ込めない。
        // 予定を書いている最中に本体が消えると、書き終わって戻る先が無くなる
        if (OwnsForeground()) return;

        _window.Hide();
    }

    /// <summary>いま窓が乗っているモニタ全体（物理ピクセル）。</summary>
    private NativeMethods.RECT ScreenOfWindow()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        var scale = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice.M11
            ?? 1.0;

        return Screens.Of(handle, scale);
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

        var scale = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice.M11
            ?? 1.0;
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

        // 留める前に控えた値があればそちらを使い、使ったら捨てる。次に出すときには
        // ワークエリアも戻っているので、そのときは素直に測ってよい
        var work = _workBeforeDock ?? SystemParameters.WorkArea;
        _workBeforeDock = null;

        var width = _shell.DockWidth;

        _window.Top = work.Top;
        _window.Height = work.Height;
        _window.Width = width;
        _window.Left = _shell.Edge == DockEdge.Left ? work.Left : work.Right - width;
    }
}
