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

            // 手が止まってから交渉する
            _resizeSettle.Stop();
            _resizeSettle.Start();
        };

        // 帯に留まったら前に出す
        _hotZone.Triggered += (_, _) => Show();

        // 全画面アプリなどで外れたら、見た目も合わせる
        _appBar.Undocked += (_, _) =>
        {
            if (_shell.IsPinned) _shell.Mode = ShellMode.Overlay;
        };
    }

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
                ToWindow();
                break;

            case ShellMode.Overlay:
                _appBar.Undock();
                ToEdge();
                ApplyOverlayBounds();

                // ピン留め中は張らない。常時出ているので呼び出す口が要らない
                _hotZone.Arm(_shell.Edge);
                break;

            case ShellMode.Dock:
                _hotZone.Disarm();
                ToEdge();

                // 削れなければオーバーレイに落とす。黙って重なったままにしない
                if (!_appBar.Dock(_shell.Edge, _shell.DockWidth)) _shell.Mode = ShellMode.Overlay;
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
    /// オーバーレイの位置。
    /// <para>ワークエリアは削らないので、こちらで画面端に合わせる。</para>
    /// </summary>
    private void ApplyOverlayBounds()
    {
        if (!_shell.IsAtEdge || _shell.IsPinned) return;

        var work = SystemParameters.WorkArea;
        var width = _shell.DockWidth;

        _window.Top = work.Top;
        _window.Height = work.Height;
        _window.Width = width;
        _window.Left = _shell.Edge == DockEdge.Left ? work.Left : work.Right - width;
    }
}
