using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>サイドバーの下半分に出すもの。幅が無いので上下に分けずタブにする（要件書 5.3）。</summary>
public enum SidebarTab
{
    Events,
    Tasks,
}

/// <summary>
/// 画面での居かた（要件書 2章）。
/// <para>
/// ウィンドウ → オーバーレイ → ドックの行き来を持つ。<b>実際に画面へ効かせるのは
/// アプリ側</b>（AppBar の登録やホットゾーンの張り方は Win32 の話なので、ここには
/// 置かない）。ここが持つのは「どうしたいか」だけで、変わったことを知らせる。
/// </para>
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private ShellMode _mode;
    private DockEdge _edge;
    private double _dockWidth;
    private SidebarTab _tab = SidebarTab.Events;
    private double _layoutWidth = double.NaN;

    /// <summary>ピンを外したときに戻る先。留める前の居かたを覚えておく。</summary>
    private ShellMode _beforePin = ShellMode.Window;

    public ShellViewModel(DockPlacement placement)
    {
        var usable = placement.WithUsableWidth();

        _mode = usable.Mode;
        _edge = usable.Edge;
        _dockWidth = usable.Width;

        TogglePinCommand = new RelayCommand(TogglePin);
        ToWindowCommand = new RelayCommand(() => Mode = ShellMode.Window);
        ToOverlayCommand = new RelayCommand(() => Mode = ShellMode.Overlay);
        ToggleEdgeCommand = new RelayCommand(
            () => Edge = _edge == DockEdge.Left ? DockEdge.Right : DockEdge.Left);
        ShowEventsCommand = new RelayCommand(() => Tab = SidebarTab.Events);
        ShowTasksCommand = new RelayCommand(() => Tab = SidebarTab.Tasks);
    }

    /// <summary>居かたが変わった。アプリ側が受けて実際に動かす。</summary>
    public event EventHandler<ShellMode>? ModeChanged;

    /// <summary>寄せる辺が変わった。</summary>
    public event EventHandler<DockEdge>? EdgeChanged;

    /// <summary>ドックの幅が変わった。ドラッグ中は呼ばれない（確定してから）。</summary>
    public event EventHandler<double>? DockWidthChanged;

    public ShellMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;

            Raise(nameof(IsWindowMode), nameof(IsOverlayMode), nameof(IsPinned),
                nameof(IsAtEdge), nameof(PinLabel));
            ModeChanged?.Invoke(this, value);
        }
    }

    public DockEdge Edge
    {
        get => _edge;
        set
        {
            if (!Set(ref _edge, value)) return;

            Raise(nameof(IsAtLeft), nameof(IsAtRight));
            EdgeChanged?.Invoke(this, value);
        }
    }

    /// <summary>ドックの幅。範囲に収めてから入る。</summary>
    public double DockWidth
    {
        get => _dockWidth;
        set
        {
            var width = double.IsNaN(value) || double.IsInfinity(value)
                ? DockPlacement.DefaultWidth
                : Math.Clamp(value, DockPlacement.MinWidth, DockPlacement.MaxWidth);

            if (!Set(ref _dockWidth, width)) return;

            DockWidthChanged?.Invoke(this, width);
        }
    }

    /// <summary>サイドバーの下半分に出しているもの。</summary>
    public SidebarTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;

            Raise(nameof(ShowsEvents), nameof(ShowsTasks));
        }
    }

    /// <summary>
    /// いまの見た目の幅。
    /// <para>
    /// これを境にレイアウトを切り替える。同じ UI を縮小して使い回さない（要件書 5.1）。
    /// </para>
    /// </summary>
    public double LayoutWidth
    {
        get => _layoutWidth;
        set
        {
            if (!Set(ref _layoutWidth, value)) return;

            Raise(nameof(UsesSidebarLayout), nameof(UsesWindowLayout));
        }
    }

    /// <summary>
    /// 1列の形（上にカレンダー、下にタブ）にするか。
    /// <para>この幅で3ペインに分けると、各ペインが3行しか入らず実用にならない。</para>
    /// </summary>
    public bool UsesSidebarLayout =>
        !double.IsNaN(_layoutWidth) && _layoutWidth > 0 && _layoutWidth < DockPlacement.SidebarThreshold;

    /// <summary>3ペインの形にするか。</summary>
    public bool UsesWindowLayout => !UsesSidebarLayout;

    public bool IsWindowMode => _mode == ShellMode.Window;

    public bool IsOverlayMode => _mode == ShellMode.Overlay;

    /// <summary>ピン留め中。ワークエリアを削っている。</summary>
    public bool IsPinned => _mode == ShellMode.Dock;

    /// <summary>画面端に貼り付いている。オーバーレイとドックの両方。</summary>
    public bool IsAtEdge => _mode is ShellMode.Overlay or ShellMode.Dock;

    public bool IsAtLeft => _edge == DockEdge.Left;

    public bool IsAtRight => _edge == DockEdge.Right;

    public bool ShowsEvents => _tab == SidebarTab.Events;

    public bool ShowsTasks => _tab == SidebarTab.Tasks;

    /// <summary>ピンボタンの説明。押すと何が起きるかを書く。</summary>
    public string PinLabel => IsPinned
        ? "ピンを外す（画面の分割をやめ、元の出しかたに戻す）"
        : "ピン留めする（画面端に寄せて画面を分割し、他のウィンドウと重ならないようにする）";

    /// <summary>いまの居場所。終了時に控える。</summary>
    public DockPlacement Placement(string? monitorId = null) =>
        new(_mode, _edge, _dockWidth, monitorId);

    /// <summary>
    /// ピンを切り替える。
    /// <para>
    /// <b>ウィンドウからでもひと押しで留まる。</b>要件書 2.1 は「端へドラッグして
    /// オーバーレイ → ピンでドック」という順だが、端へ寄せる操作は Windows の
    /// スナップ（画面の半分に広がる）と取り合いになって当てにならない。ボタンを
    /// 常時出し、押したらそのまま画面の分割まで行く。
    /// </para>
    /// <para>外すときは、留める前の居かたへ戻す。</para>
    /// </summary>
    private void TogglePin()
    {
        if (_mode == ShellMode.Dock)
        {
            Mode = _beforePin;
            return;
        }

        _beforePin = _mode;
        Mode = ShellMode.Dock;
    }

    public RelayCommand TogglePinCommand { get; }

    public RelayCommand ToWindowCommand { get; }

    public RelayCommand ToOverlayCommand { get; }

    public RelayCommand ToggleEdgeCommand { get; }

    public RelayCommand ShowEventsCommand { get; }

    public RelayCommand ShowTasksCommand { get; }
}
