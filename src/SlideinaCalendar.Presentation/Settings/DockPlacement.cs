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
/// <param name="Width">
/// 端寄せ（スライド・ピン留め）で使う幅。
/// <para>
/// スライドとピン留めは表示内容も幅も同じ、出しかた（消えるか・居座るか）だけが違う
/// ものとして扱う。<b>この2つで別々の幅は持たない。</b>ウィンドウの大きさは
/// <see cref="WindowPlacement"/> 側で別に控える。
/// </para>
/// </param>
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
    /// この幅を境に、1列の形（上にカレンダー・下にタブ）へ切り替える。
    /// <para>
    /// 同じ UI を縮小して使い回さない、という考え方（要件書 5.1）で 520px に
    /// していたが、狭いときはパネルを左 → 右 の順に畳む作りにしたので、
    /// <b>別の形へ化けるのは本当に置き場が無いときだけ</b>にした。途中で
    /// 見た目が丸ごと入れ替わると、同じ操作を探し直すことになる。
    /// </para>
    /// </summary>
    public const double SidebarThreshold = 300;

    /// <summary>覚えていないとき。既定は左。</summary>
    public static DockPlacement Unknown { get; } =
        new(ShellMode.Window, DockEdge.Left, DefaultWidth, null);

    /// <summary>ワークエリアを削っている状態か。</summary>
    public bool ReservesWorkArea => Mode == ShellMode.Dock;

    /// <summary>画面端に貼り付く状態か。オーバーレイとドックの両方。</summary>
    public bool IsAtEdge => Mode is ShellMode.Overlay or ShellMode.Dock;

    /// <summary>幅を使える範囲に収める。</summary>
    public DockPlacement WithUsableWidth() => this with { Width = Usable(Width) };

    /// <summary>使える範囲に収めた幅。</summary>
    public static double Usable(double width) =>
        double.IsNaN(width) || double.IsInfinity(width)
            ? DefaultWidth
            : Math.Clamp(width, MinWidth, MaxWidth);

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

    /// <summary>
    /// 旧バージョンにあった、固定（ピン留め）だけの幅。
    /// <para>
    /// いまはスライドとピン留めで幅を1つに畳んだので書かなくなったが、以前の版で
    /// 控えたぶんが端末に残っていることがある。読み込み時だけ見て、残っていれば
    /// <see cref="WidthKey"/> へ引き継ぐ（<see cref="Load"/>）。
    /// </para>
    /// </summary>
    private const string LegacyDockedWidthKey = "shell.docked_width";

    /// <summary>ワークエリアを削っている最中か。きれいに終われば false に戻る。</summary>
    private const string ReservedKey = "shell.work_area_reserved";

    private readonly SettingsRepository _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>前回の居場所。</summary>
    public DockPlacement Load()
    {
        // 移行：スライドと固定の幅を分けて持っていた版からの引き継ぎ。
        // 固定の幅のほうを残す。利用者が最後に画面を分けて使っていた形に近いため。
        // 捨てて既定値に戻すと、使っていた幅が消えてしまう
        var legacyDocked = _store.Get(LegacyDockedWidthKey);

        var width =
            double.TryParse(legacyDocked, NumberStyles.Float, CultureInfo.InvariantCulture, out var docked)
                ? docked
                : double.TryParse(_store.Get(WidthKey), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var stored)
                    ? stored
                    : DockPlacement.DefaultWidth;

        if (legacyDocked is { Length: > 0 })
        {
            // 引き継いだので、古いキーはもう要らない
            _store.Set(WidthKey, width.ToString("R", CultureInfo.InvariantCulture));
            _store.Remove(LegacyDockedWidthKey);
        }

        var placement = new DockPlacement(
            Enum.TryParse<ShellMode>(_store.Get(ModeKey), ignoreCase: true, out var mode)
                && Enum.IsDefined(mode) ? mode : ShellMode.Window,
            Enum.TryParse<DockEdge>(_store.Get(EdgeKey), ignoreCase: true, out var edge)
                && Enum.IsDefined(edge) ? edge : DockEdge.Left,
            width,
            _store.Get(MonitorKey) is { Length: > 0 } monitor ? monitor : null);

        return placement.WithUsableWidth();
    }

    /// <summary>
    /// 居場所を控える。
    /// <para>
    /// 4つのキーを<b>1トランザクションにまとめて</b>書く。別々の文で書くと、途中で
    /// 落ちたときに「モード＝Dock なのに幅は前の値」のような食い違った組み合わせが
    /// 残りうる。
    /// </para>
    /// </summary>
    public void Save(DockPlacement placement)
    {
        _store.SetMany(new Dictionary<string, string>
        {
            [ModeKey] = placement.Mode.ToString(),
            [EdgeKey] = placement.Edge.ToString(),
            [WidthKey] = placement.Width.ToString("R", CultureInfo.InvariantCulture),
            [MonitorKey] = placement.MonitorId ?? string.Empty,
        });
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
