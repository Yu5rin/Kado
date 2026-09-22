using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;

namespace Kado.Presentation.ViewModels;

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
    private double _width;
    private SidebarTab _tab = SidebarTab.Events;
    private double _layoutWidth = double.NaN;

    /// <summary>ピンを外したときに戻る先。留める前の居かたを覚えておく。</summary>
    private ShellMode _beforePin = ShellMode.Window;

    public ShellViewModel(DockPlacement placement)
    {
        var usable = placement.WithUsableWidth();

        _mode = usable.Mode;
        _edge = usable.Edge;
        _width = usable.Width;

        TogglePinCommand = new RelayCommand(TogglePin);
        ToWindowCommand = new RelayCommand(() => Mode = ShellMode.Window);
        ToOverlayCommand = new RelayCommand(() => Mode = ShellMode.Overlay);
        ToggleEdgeCommand = new RelayCommand(
            () => Edge = _edge == DockEdge.Left ? DockEdge.Right : DockEdge.Left);
        EdgeLeftCommand = new RelayCommand(() => Edge = DockEdge.Left);
        EdgeRightCommand = new RelayCommand(() => Edge = DockEdge.Right);
        ToggleSlideCommand = new RelayCommand(
            () => Mode = _mode == ShellMode.Window ? ShellMode.Overlay : ShellMode.Window);
        ShowEventsCommand = new RelayCommand(() => Tab = SidebarTab.Events);
        ShowTasksCommand = new RelayCommand(() => Tab = SidebarTab.Tasks);
    }

    /// <summary>居かたが変わった。アプリ側が受けて実際に動かす。</summary>
    public event EventHandler<ShellMode>? ModeChanged;

    /// <summary>寄せる辺が変わった。</summary>
    public event EventHandler<DockEdge>? EdgeChanged;

    /// <summary>ドックの幅が変わった。ドラッグ中は呼ばれない（確定してから）。</summary>
    public event EventHandler<double>? DockWidthChanged;

    /// <summary>
    /// 今すぐ引っ込めてほしい（Esc など）。アプリ側（<c>ShellController</c>）が受けて、
    /// マウスが外れたときと同じ経路で引っ込める。
    /// <para>
    /// 実際に画面を動かすのはここではなく <c>ShellController</c> の役目。<c>MainWindow</c>
    /// は <c>ShellController</c> を直に持たないため、この <see cref="ShellViewModel"/> 越しに頼む
    /// （<see cref="ModeChanged"/> などと同じ配線）。
    /// </para>
    /// </summary>
    public event EventHandler? RetractRequested;

    /// <summary>Esc などから呼ぶ。ウィンドウ居かたでは、そもそも引っ込める意味が無い。</summary>
    public void RequestRetract()
    {
        if (Mode == ShellMode.Window) return;

        RetractRequested?.Invoke(this, EventArgs.Empty);
    }

    public ShellMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;

            Raise(nameof(IsWindowMode), nameof(IsOverlayMode), nameof(IsPinned),
                nameof(IsAtEdge), nameof(PinLabel), nameof(ModeLabel), nameof(ModeToggleLabel),
                nameof(SlideActionLabel), nameof(DockWidth));
            ModeChanged?.Invoke(this, value);
        }
    }

    public DockEdge Edge
    {
        get => _edge;
        set
        {
            if (!Set(ref _edge, value)) return;

            Raise(nameof(IsAtLeft), nameof(IsAtRight), nameof(ModeLabel), nameof(ModeToggleLabel));
            EdgeChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// 端寄せ（スライド・ピン留め）のときの幅。範囲に収めてから入る。
    /// <para>
    /// <b>スライドとピン留めは同じ幅を使う。</b>表示内容も幅も揃え、違うのは
    /// 出しかた（消えるか・居座るか）だけにした。切り替えても幅が変わらないので、
    /// ピンを外してスライドにしても、逆にしても同じ幅のまま出る。ウィンドウの
    /// 大きさはまた別に控えてある（<see cref="WindowPlacement"/>）。
    /// </para>
    /// </summary>
    public double DockWidth
    {
        get => _width;
        set
        {
            var width = double.IsNaN(value) || double.IsInfinity(value)
                ? DockPlacement.DefaultWidth
                : Math.Clamp(value, _minWidth, DockPlacement.MaxWidth);

            if (!Set(ref _width, width, nameof(DockWidth))) return;

            DockWidthChanged?.Invoke(this, width);
        }
    }

    /// <summary>
    /// 幅をつまんでいる最中か。
    /// <para>
    /// スライドは、カーソルが窓から外れたら引っ込む。幅を狭める向きに引くと
    /// つまんでいる手そのものが窓の外へ出るので、そのままだと必ず消える。
    /// <b>つまんでいるあいだは引っ込めない。</b>
    /// </para>
    /// </summary>
    public bool IsResizing
    {
        get => _isResizing;
        set => Set(ref _isResizing, value);
    }

    private bool _isResizing;

    /// <summary>
    /// いちばん細くできる幅。設定から受ける。
    /// <para>
    /// <b>窓の下限と揃えておく。</b>片方だけ下げても、もう片方が押し戻すので
    /// そこまで細くならない。設定で 160px にしたのに縮まない、という形で出る。
    /// </para>
    /// </summary>
    public double MinWidth
    {
        get => _minWidth;
        set
        {
            var width = Math.Clamp(value, 100, DockPlacement.MaxWidth);

            if (!Set(ref _minWidth, width)) return;

            // いまの幅が下限を割っていたら引き上げる
            DockWidth = DockWidth;
        }
    }

    private double _minWidth = DockPlacement.MinWidth;

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
        ? "ピンを外す（留める前の出しかたに戻す。Ctrl＋Alt＋P）"
        : "ピン留めする（出したまま固定し、画面を分割する。Ctrl＋Alt＋P）";

    /// <summary>いまの出しかたの名前。ボタンに出す。</summary>
    public string ModeLabel => _mode switch
    {
        ShellMode.Overlay => _edge == DockEdge.Left ? "スライド（左）" : "スライド（右）",
        ShellMode.Dock => _edge == DockEdge.Left ? "固定（左）" : "固定（右）",
        _ => "ウィンドウ",
    };

    /// <summary>
    /// ▥ ボタンのツールチップ。
    /// <para>
    /// <see cref="PinLabel"/> と粒度を揃える。いまの状態名だけでは、左クリックと
    /// 右クリックで別の動きをすることが伝わらない（項目14）。
    /// </para>
    /// </summary>
    public string ModeToggleLabel =>
        $"{ModeLabel}（左クリックでウィンドウ⇔スライドを切替 ・ 右クリックで端の選択とパネルの出し入れ）";

    /// <summary>
    /// 出しかたボタン（項目1）のツールチップ。
    /// <para>
    /// 状態名（<see cref="ModeLabel"/>）ではなく、押したら何が起きるかを短く書く。
    /// パネルの出し入れは別のボタン（IconPanes）の役目になったので、ここは
    /// ウィンドウ⇔スライドの切り替えだけを伝える。
    /// </para>
    /// </summary>
    public string SlideActionLabel => IsAtEdge
        ? "ウィンドウに戻す"
        : "スライドにする（Ctrl＋Alt＋S）";

    /// <summary>いまの居場所。終了時に控える。</summary>
    public DockPlacement Placement(string? monitorId = null) => new(_mode, _edge, _width, monitorId);

    /// <summary>
    /// ピンを切り替える。
    /// <para>
    /// <b>ウィンドウからでもひと押しで留まる。</b>要件書 2.1 は「端へドラッグして
    /// オーバーレイ → ピンでドック」という順だが、端へ寄せる操作は Windows の
    /// スナップ（画面の半分に広がる）と取り合いになって当てにならない。ボタンを
    /// 常時出し、押したらそのまま画面の分割まで行く。
    /// </para>
    /// <para>
    /// 外すときは、<b>留める前の居かたへそのまま戻す</b>。ウィンドウから留めたのに
    /// スライドで返すと、画面から消えてしまって戻し方が分からなくなる。
    /// </para>
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

    /// <summary>
    /// 出す位置を左端にする。
    /// <para>
    /// <b>位置を決めるだけで、出しかたは変えない。</b>ウィンドウとスライドの
    /// 行き来はボタンの左クリック（<see cref="ToggleSlideCommand"/>）の役目で、
    /// 右クリックのメニューは「どちらの端から出すか」を選ぶところ。
    /// </para>
    /// </summary>
    public RelayCommand EdgeLeftCommand { get; }

    /// <summary>出す位置を右端にする。</summary>
    public RelayCommand EdgeRightCommand { get; }

    /// <summary>ウィンドウとスライドを行き来する。</summary>
    public RelayCommand ToggleSlideCommand { get; }

    public RelayCommand ShowEventsCommand { get; }

    public RelayCommand ShowTasksCommand { get; }
}
