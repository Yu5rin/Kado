using System.Globalization;
using Kado.Data.Repositories;

namespace Kado.Presentation.Settings;

/// <summary>
/// ウィンドウの置き場所と大きさ。
/// <para>
/// 最大化していても<b>元に戻したときの大きさ</b>を一緒に覚える。覚えないと、最大化で
/// 終わった次の起動で元に戻したときに既定の大きさへ落ちる。WPF では
/// <c>Window.RestoreBounds</c> が、最大化中でもこの値を持っている。
/// </para>
/// <para>
/// 最小化した状態は覚えない。次の起動でアイコンのまま出てくると、立ち上がったのか
/// どうか分からない。
/// </para>
/// </summary>
/// <param name="Left">左端。覚えていなければ <see cref="double.NaN"/>。</param>
/// <param name="Top">上端。覚えていなければ <see cref="double.NaN"/>。</param>
/// <param name="Width">元に戻したときの幅。</param>
/// <param name="Height">元に戻したときの高さ。</param>
/// <param name="IsMaximized">最大化で終わったか。</param>
public readonly record struct WindowPlacement(
    double Left, double Top, double Width, double Height, bool IsMaximized)
{
    /// <summary>まだ何も覚えていないときの大きさ。MainWindow.xaml の既定に合わせる。</summary>
    public const double DefaultWidth = 1180;

    /// <inheritdoc cref="DefaultWidth"/>
    public const double DefaultHeight = 760;

    /// <summary>これより小さい大きさは受け取らない。MainWindow.xaml の下限に合わせる。</summary>
    public const double MinWidth = 280;

    /// <inheritdoc cref="MinWidth"/>
    public const double MinHeight = 520;

    /// <summary>覚えていないとき。大きさだけ既定を持ち、置き場所は持たない。</summary>
    public static WindowPlacement Unknown { get; } =
        new(double.NaN, double.NaN, DefaultWidth, DefaultHeight, false);

    /// <summary>置き場所を覚えているか。無ければ画面の真ん中に出す。</summary>
    public bool HasPosition => IsReal(Left) && IsReal(Top);

    /// <summary>下限を下回らない大きさに直す。</summary>
    public WindowPlacement WithUsableSize() => this with
    {
        Width = IsReal(Width) ? Math.Max(MinWidth, Width) : DefaultWidth,
        Height = IsReal(Height) ? Math.Max(MinHeight, Height) : DefaultHeight,
    };

    /// <summary>
    /// 画面の中に収める。
    /// <para>
    /// <b>前に使っていた画面が無くなっていることがある。</b>会社ではノートを外付けの
    /// ディスプレイに繋いで使い、持ち出すときに外す。繋いでいたときの位置のまま出すと、
    /// 画面の外に開いて手が出せなくなる。
    /// </para>
    /// <para>
    /// 大きさが画面より大きければ縮め、はみ出していれば押し戻す。置き場所を覚えて
    /// いなければ何もしない（呼び出し側が真ん中に出す）。
    /// </para>
    /// </summary>
    /// <param name="left">画面全体の左端。複数枚あるときは、いちばん左の画面の左端。</param>
    /// <param name="top">画面全体の上端。</param>
    /// <param name="width">画面全体の幅。</param>
    /// <param name="height">画面全体の高さ。</param>
    public WindowPlacement ClampTo(double left, double top, double width, double height)
    {
        if (width <= 0 || height <= 0) return this;

        var placement = WithUsableSize();

        // 画面より大きければ縮める。ただし下限は割らない
        placement = placement with
        {
            Width = Math.Max(MinWidth, Math.Min(placement.Width, width)),
            Height = Math.Max(MinHeight, Math.Min(placement.Height, height)),
        };

        if (!placement.HasPosition) return placement;

        return placement with
        {
            Left = Push(placement.Left, placement.Width, left, width),
            Top = Push(placement.Top, placement.Height, top, height),
        };
    }

    /// <summary>はみ出したぶんを押し戻す。窓のほうが大きいときは端に合わせる。</summary>
    private static double Push(double start, double length, double limitStart, double limitLength)
    {
        var last = limitStart + limitLength - length;

        return last <= limitStart ? limitStart : Math.Clamp(start, limitStart, last);
    }

    private static bool IsReal(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// ウィンドウの置き場所の出し入れ。
/// <para>
/// 設定と同じ <c>settings</c> 表に入れるが、<see cref="AppSettings"/> には置かない。
/// 設定画面から触るものではなく、閉じるたびに黙って書き換わる値なので、変わったことを
/// 知らせる必要もない。
/// </para>
/// </summary>
public sealed class WindowPlacementStore(SettingsRepository store)
{
    private const string LeftKey = "window.left";
    private const string TopKey = "window.top";
    private const string WidthKey = "window.width";
    private const string HeightKey = "window.height";
    private const string MaximizedKey = "window.maximized";

    private readonly SettingsRepository _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>前回の置き場所。覚えていなければ <see cref="WindowPlacement.Unknown"/>。</summary>
    public WindowPlacement Load()
    {
        var placement = new WindowPlacement(
            Number(LeftKey), Number(TopKey),
            Number(WidthKey), Number(HeightKey),
            string.Equals(_store.Get(MaximizedKey), bool.TrueString, StringComparison.OrdinalIgnoreCase));

        return placement.WithUsableSize();
    }

    /// <summary>置き場所を控える。</summary>
    public void Save(WindowPlacement placement)
    {
        Set(LeftKey, placement.Left);
        Set(TopKey, placement.Top);
        Set(WidthKey, placement.Width);
        Set(HeightKey, placement.Height);
        _store.Set(MaximizedKey, placement.IsMaximized ? bool.TrueString : bool.FalseString);
    }

    private double Number(string key) =>
        double.TryParse(_store.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var saved)
            ? saved
            : double.NaN;

    private void Set(string key, double value) =>
        _store.Set(key, double.IsNaN(value) || double.IsInfinity(value)
            ? string.Empty
            : value.ToString("R", CultureInfo.InvariantCulture));
}
