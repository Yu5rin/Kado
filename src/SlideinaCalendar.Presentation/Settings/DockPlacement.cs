using System.Globalization;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.Settings;

/// <summary>
/// 画面での居かた（要件書 2.1）。
/// <para>
/// ウィンドウ → オーバーレイ → ドックの3段。オーバーレイは他のウィンドウの上に
/// 重なるだけで、ドックにして初めてワークエリアを削り、最大化した他のウィンドウと
/// 重ならなくなる。
/// </para>
/// <para>
/// Windows では自動非表示の AppBar はワークエリアを削らない。出入りのたびに
/// 登録・解除すれば理屈の上では削れるが、そのたびに全ウィンドウがリサイズされて
/// 実用にならない。だから「ホバーで呼び出し、ピンを押すと画面分割に切り替わる」
/// という2段構えにしてある。
/// </para>
/// </summary>
public enum ShellMode
{
    /// <summary>ふつうのウィンドウ。全ビューを使える。</summary>
    Window,

    /// <summary>画面端にマウスを当てるとスライドインする。ワークエリアは削らない。</summary>
    Overlay,

    /// <summary>画面端に常駐し、ワークエリアを削る。最大化した他のウィンドウと重ならない。</summary>
    Dock,
}

/// <summary>寄せる辺。上下は横幅を取りすぎるので持たない。</summary>
public enum DockEdge
{
    Left,
    Right,
}

/// <summary>
/// ドックの居場所。
/// <para>終了時に控え、次の起動で戻す（要件書 2.2）。</para>
/// </summary>
/// <param name="Mode">ウィンドウ／オーバーレイ／ドック。</param>
/// <param name="Edge">どちらの辺に寄せるか。</param>
/// <param name="Width">ドックの幅。</param>
/// <param name="MonitorId">
/// どのモニタへ寄せるか。<c>\\.\DISPLAY1</c> のような識別子。
/// <para>覚えていなければ null（主モニタへ寄せる）。</para>
/// </param>
public readonly record struct DockPlacement(
    ShellMode Mode, DockEdge Edge, double Width, string? MonitorId)
{
    /// <summary>サイドバーモードの既定幅（要件書 5.3）。</summary>
    public const double DefaultWidth = 352;

    /// <summary>これより狭いと中身が読めない。</summary>
    public const double MinWidth = 280;

    /// <summary>画面の半分を超えて削ると、元の作業が成り立たない。</summary>
    public const double MaxWidth = 720;

    /// <summary>
    /// この幅を境にレイアウトを切り替える。
    /// <para>
    /// 同じ UI を縮小して使い回さない（要件書 5.1）。これより狭ければ、上に
    /// カレンダー・下にタブという1列の形にする。
    /// </para>
    /// </summary>
    public const double SidebarThreshold = 520;

    /// <summary>覚えていないとき。既定は左。</summary>
    public static DockPlacement Unknown { get; } =
        new(ShellMode.Window, DockEdge.Left, DefaultWidth, null);

    /// <summary>ワークエリアを削っている状態か。</summary>
    public bool ReservesWorkArea => Mode == ShellMode.Dock;

    /// <summary>画面端に貼り付く状態か。オーバーレイとドックの両方。</summary>
    public bool IsAtEdge => Mode is ShellMode.Overlay or ShellMode.Dock;

    /// <summary>幅を使える範囲に収める。</summary>
    public DockPlacement WithUsableWidth() => this with
    {
        Width = double.IsNaN(Width) || double.IsInfinity(Width)
            ? DefaultWidth
            : Math.Clamp(Width, MinWidth, MaxWidth),
    };

    /// <summary>
    /// 画面の幅に対して広すぎないか見て、必要なら縮める。
    /// <para>削りすぎると、元の作業をする場所が残らない。半分までにする。</para>
    /// </summary>
    public DockPlacement ClampTo(double screenWidth)
    {
        var placement = WithUsableWidth();

        if (screenWidth <= 0) return placement;

        var half = screenWidth / 2;

        // 半分より狭くできないほど画面が小さいなら、下限を優先する。
        // 中身が読めないほど細い帯を置いても仕方がない
        return placement with { Width = Math.Max(MinWidth, Math.Min(placement.Width, half)) };
    }
}

/// <summary>
/// ドックの居場所の出し入れ。
/// <para>
/// <b>ワークエリアを削ったかどうかも控える。</b><c>ABM_REMOVE</c> を呼ばずに
/// プロセスが落ちると、削られたまま残ってデスクトップが壊れる。次の起動で
/// 「前回は削ったまま終わった」と分かるようにしておき、元に戻す（要件書 2.3）。
/// </para>
/// </summary>
public sealed class DockPlacementStore(SettingsRepository store)
{
    private const string ModeKey = "shell.mode";
    private const string EdgeKey = "shell.edge";
    private const string WidthKey = "shell.width";
    private const string MonitorKey = "shell.monitor";

    /// <summary>ワークエリアを削っている最中か。きれいに終われば false に戻る。</summary>
    private const string ReservedKey = "shell.work_area_reserved";

    private readonly SettingsRepository _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>前回の居場所。</summary>
    public DockPlacement Load()
    {
        var placement = new DockPlacement(
            Enum.TryParse<ShellMode>(_store.Get(ModeKey), ignoreCase: true, out var mode)
                && Enum.IsDefined(mode) ? mode : ShellMode.Window,
            Enum.TryParse<DockEdge>(_store.Get(EdgeKey), ignoreCase: true, out var edge)
                && Enum.IsDefined(edge) ? edge : DockEdge.Left,
            double.TryParse(_store.Get(WidthKey), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var width) ? width : DockPlacement.DefaultWidth,
            _store.Get(MonitorKey) is { Length: > 0 } monitor ? monitor : null);

        return placement.WithUsableWidth();
    }

    /// <summary>居場所を控える。</summary>
    public void Save(DockPlacement placement)
    {
        _store.Set(ModeKey, placement.Mode.ToString());
        _store.Set(EdgeKey, placement.Edge.ToString());
        _store.Set(WidthKey, placement.Width.ToString("R", CultureInfo.InvariantCulture));
        _store.Set(MonitorKey, placement.MonitorId ?? string.Empty);
    }

    /// <summary>
    /// 前回、ワークエリアを削ったまま終わったか。
    /// <para>true なら異常終了。ワークエリアを元に戻してから始める。</para>
    /// </summary>
    public bool WasWorkAreaLeftReserved() =>
        string.Equals(_store.Get(ReservedKey), bool.TrueString, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ワークエリアを削っている／いないを控える。
    /// <para>
    /// <b>削る前に true を書き、戻したあとに false を書く。</b>順序を逆にすると、
    /// その隙に落ちたときに記録が残らない。
    /// </para>
    /// </summary>
    public void SetWorkAreaReserved(bool reserved) =>
        _store.Set(ReservedKey, reserved ? bool.TrueString : bool.FalseString);
}
