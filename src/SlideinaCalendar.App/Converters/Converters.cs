using System.Globalization;
using SlideinaCalendar.Presentation;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Converters;

/// <summary>true なら表示、false なら畳む。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>null や空文字なら畳む。補足の行を出し分けるのに使う。</summary>
/// <summary>
/// 値が null でないかを bool にする。
/// <para>
/// ステータス帯の出入りを DataTrigger の EnterActions/ExitActions で
/// アニメーションさせるために使う。null かどうかの比較だけなら
/// <c>Binding.Value="{x:Null}"</c> で足りるが、Trigger の bool 条件として
/// 扱いたいのでここで変換する。
/// </para>
/// </summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string s when string.IsNullOrWhiteSpace(s) => Visibility.Collapsed,
            System.Collections.ICollection { Count: 0 } => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// マイルストーンの種類名から色を引く。
/// <para>
/// 名前は取り込んだ文字列そのままで、固定4種に限らない（要件書 4.1）。現行の4種は
/// 配色が決まっているので名前で引き、それ以外は名前のハッシュから既定の系列に割り当てる。
/// 見分けがつけば十分で、同じ名前なら常に同じ色になることのほうが大事。
/// </para>
/// </summary>
public sealed class MilestoneBrushConverter : IValueConverter
{
    /// <summary>面と文字、どちらを返すか。XAML から ConverterParameter で指定する。</summary>
    public const string FaceParameter = "Face";

    private static readonly string[] FallbackFaceKeys =
    [
        "MilestoneSpecFaceBrush", "MilestoneGoFaceBrush", "MilestoneSFaceBrush", "MilestoneMFaceBrush",
    ];

    private static readonly string[] FallbackTextKeys =
    [
        "MilestoneSpecTextBrush", "MilestoneGoTextBrush", "MilestoneSTextBrush", "MilestoneMTextBrush",
    ];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value switch
        {
            Milestone milestone => milestone.Name,
            string text => text,
            _ => null,
        };

        if (name is null) return null;

        var wantFace = string.Equals(parameter as string, FaceParameter, StringComparison.Ordinal);
        var key = ResolveKey(name, wantFace);

        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string ResolveKey(string name, bool wantFace) => name switch
    {
        "仕様期限" => wantFace ? "MilestoneSpecFaceBrush" : "MilestoneSpecTextBrush",
        "1次GO" => wantFace ? "MilestoneGoFaceBrush" : "MilestoneGoTextBrush",
        "S中日程" => wantFace ? "MilestoneSFaceBrush" : "MilestoneSTextBrush",
        "M中日程" => wantFace ? "MilestoneMFaceBrush" : "MilestoneMTextBrush",

        // 実働日データから起こす2つの印。名前のハッシュ任せにすると、
        // 他の名前と同じ色になったり、種類の追加で色が入れ替わったりする
        CalendarWorkspace.ClosedDayTitle => wantFace ? "ClosedDayFaceBrush" : "ClosedDayTextBrush",
        CalendarWorkspace.OpenDayTitle => wantFace ? "OpenDayFaceBrush" : "OpenDayTextBrush",

        // 知らない名前。名前から決まる添字を使い、同じ名前には常に同じ色を割り当てる
        _ => (wantFace ? FallbackFaceKeys : FallbackTextKeys)[StableIndex(name, FallbackFaceKeys.Length)],
    };

    private static int StableIndex(string name, int count)
    {
        // string.GetHashCode は実行ごとに変わるため使えない。色が毎回変わってしまう
        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        return Math.Abs(hash % count);
    }
}

/// <summary>
/// 年ビューの印の色。
/// <para>
/// 日付の行の印（仕様期限・1次GO など）は名前ごとに色が決まっている。ふつうの
/// 予定は所属カレンダーの色。どちらなのかは印自身が持っている。
/// </para>
/// </summary>
public sealed class DayMarkBrushConverter : IValueConverter
{
    private static readonly MilestoneBrushConverter Milestones = new();
    private static readonly EventColorBrushConverter Events = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SlideinaCalendar.Presentation.ViewModels.DayMark mark) return null;

        return mark.MilestoneName is { Length: > 0 } name
            ? Milestones.Convert(name, targetType, parameter!, culture)
            : Events.Convert(mark.Color!, targetType, parameter!, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>期限の強調度から文字色を引く。</summary>
public sealed class DueEmphasisBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current?.TryFindResource(value switch
        {
            DueEmphasis.Overdue => "SundayBrush",   // 超過は赤（要件書 4.4）
            DueEmphasis.Today => "AccentBrush",
            _ => "Ink3Brush",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>予定の開始時刻。終日なら「終日」。</summary>
public sealed class EventTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TimeOnly time ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : "終日";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 予定の色から帯のブラシを作る。
/// <para>
/// 色は所属カレンダーで決まる（<c>ICalendarPalette</c>）。決まっていなければ
/// 既定のアクセント色に倒す。
/// </para>
/// </summary>
public sealed class EventColorBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ParseColor(value) is { } color
            ? new SolidColorBrush(color)
            : Application.Current?.TryFindResource("AccentBrush");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>壊れた色でも表示は続ける。既定の色に倒すだけで済ませる。</summary>
    internal static Color? ParseColor(object? value)
    {
        if (value is not string hex || hex.Length == 0) return null;

        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// 予定の色から面のブラシを作る。
/// <para>帯の色を薄く敷く。モックは rgba で 10〜12% の不透明度を使っている。</para>
/// </summary>
public sealed class EventColorFaceConverter : IValueConverter
{
    /// <summary>帯の色を敷くときの濃さ。モックの rgba(...,.1) 相当。</summary>
    private const byte FaceAlpha = 28;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (EventColorBrushConverter.ParseColor(value) is not { } color)
        {
            // 既定の藍には専用の面色がある
            return Application.Current?.TryFindResource("AccentSoftBrush");
        }

        return new SolidColorBrush(Color.FromArgb(FaceAlpha, color.R, color.G, color.B));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <c>#rrggbb</c> の文字列からブラシを作る。
/// <para>カレンダーの色見本に使う。色はデータ側が文字列で持っているため。</para>
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length == 0) return null;

        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            // 取り込んだ色が壊れていても表示は続ける。見本が出ないだけで済ませる
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>false なら表示、true なら畳む。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// 列挙値が <c>ConverterParameter</c> と一致するか。
/// <para>ビュー切替のセグメントを、コマンドを挟まずに双方向で結ぶのに使う。</para>
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);

    /// <summary>チェックが入ったときだけ値を返す。外れたときは他のボタンが値を入れる。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name
            ? Enum.Parse(targetType, name)
            : Binding.DoNothing;
}

/// <summary>
/// <see cref="DateOnly"/> と <see cref="DateTime"/> をつなぐ。
/// <para>
/// WPF の DatePicker は <see cref="DateTime"/> しか扱えない。モデル側を
/// DateTime に寄せると時刻の無い日付に 0 時が付いて回るので、ここで変換する。
/// </para>
/// </summary>
public sealed class DateOnlyToDateTimeConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateOnly date ? date.ToDateTime(TimeOnly.MinValue) : null;

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime dateTime ? DateOnly.FromDateTime(dateTime) : null;
}

/// <summary>
/// 時間軸の上端からの位置を余白に変える。
/// <para>
/// ブロックは横に伸ばしたいので Canvas には置けない。上端からの距離を
/// <see cref="Thickness"/> の上側に入れ、左右は <c>2</c> 空けて重なりを避ける。
/// </para>
/// </summary>
public sealed class TimelineOffsetToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double top ? new Thickness(2, top, 2, 0) : new Thickness(2, 0, 2, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 幅から一定量を引き、上限で頭打ちにする。
/// <para>
/// ステータス帯の <c>MaxWidth</c> を「本体の幅 − 余白」に結ぶために使う。広い
/// ウィンドウでは既定の上限（560）で止め、狭い帯（最小160px）では
/// <c>Root.ActualWidth</c> ぴったりまで伸びて左右の縁にくっつくのを避ける。
/// <c>ConverterParameter</c> に <c>"引く量:上限"</c>（例: <c>"24:560"</c>）を渡す。
/// </para>
/// </summary>
public sealed class ShrinkWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width) return double.PositiveInfinity;

        double margin = 24, cap = double.PositiveInfinity;
        if (parameter is string s)
        {
            var parts = s.Split(':');
            if (parts.Length > 0) double.TryParse(parts[0], out margin);
            if (parts.Length > 1) double.TryParse(parts[1], out cap);
        }

        return Math.Min(cap, Math.Max(0, width - margin));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 同期の状態を丸印の色にする。
/// <para>
/// 文字を読まなくても状態が分かるようにする。ただし色だけに頼らせない。
/// 隣の文字にも同じ内容を出してある。
/// </para>
/// </summary>
public sealed class SyncStateBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as SyncState? switch
        {
            SyncState.Idle => "CategoryGreenBrush",
            SyncState.Running => "AccentBrush",
            SyncState.Warned => "CategoryAmberBrush",
            SyncState.Failed => "SundayBrush",

            // 繋いでいないときは目立たせない。異常ではないため
            _ => "Ink3Brush",
        };

        return Application.Current?.TryFindResource(key) ?? Application.Current?.TryFindResource("Ink3Brush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 同期の丸が「警告・失敗」を示しているかどうか。
/// 色だけに頼らせないよう、丸の中に「！」を出すかどうかをここで決める（項目10）。
/// </summary>
public sealed class SyncStateIsAlertConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as SyncState? is SyncState.Warned or SyncState.Failed ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// すべて true のときだけ出す。
/// <para>
/// 条件が2つ以上ある出し分けに使う。Style の中の条件で書くと、名前で指した要素が
/// 解けずに黙って出なくなることがある。こちらは Style を通らないので確実。
/// </para>
/// </summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is { Length: > 0 } && values.All(v => v is true)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// ドックグリップ（<c>DockGrip</c>）の掴みしろぶん、画面の内側の辺にだけ
/// 8px の余地を作る。
/// <para>
/// 以前は <c>DockPanel</c> 全体に <c>Margin</c> を掛けていたが、それだと
/// マージンぶんが素通りして窓の背景（<c>BackgroundBrush</c>、暗い色）が
/// そのまま見え、パネルの色との境に隙間があるように見えていた。
/// ここでは <c>Padding</c>／<c>Margin</c> の対象を「色を塗っている入れ物」
/// ではなく「その中身」に絞り、入れ物自身は窓の端まで自分の色で塗り切る。
/// </para>
/// <para>
/// どのパネルが窓の内側の辺に触れているかは、開いているパネルの並び
/// （左・中央・右）のうち先頭／末尾で決まる。ここは3つの
/// <c>bool</c> を見るだけの分岐で、<c>Style</c> の中の
/// <c>MultiDataTrigger</c> を何本も重ねるより確実（<see cref="AllTrueToVisibilityConverter"/>
/// と同じ理由）。
/// </para>
/// <para>
/// <c>ConverterParameter</c> は対象を表す文字列。<c>"Toolbar"</c> は
/// どのパネルが出ていても関係なく常に対象（ツールバーは常に窓いっぱいの帯
/// なので）。<c>"Side"</c>／<c>"Main"</c>／<c>"Detail"</c> は、それぞれの
/// パネルが実際に窓の内側の辺に触れているときだけ余地を返す（旧「スリム」
/// パネルは右パネルへ統合済み）。
/// </para>
/// </summary>
public sealed class EdgePaddingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [bool isAtEdge, bool isAtLeft, bool side, bool main, bool detail])
            return new Thickness(0);

        // 端に寄せていないときは窓自身の掴みしろがあるので余地を空けない
        if (!isAtEdge) return new Thickness(0);

        var pane = parameter as string;
        var innerLeft = new Thickness(8, 0, 0, 0);
        var innerRight = new Thickness(0, 0, 8, 0);
        var none = new Thickness(0);

        // ツールバーは窓いっぱいの1本の帯。出ているパネルに関わらず内側の辺が対象
        if (pane == "Toolbar") return isAtLeft ? innerRight : innerLeft;

        if (isAtLeft)
        {
            // 左端に寄せていれば内側は右。開いている中でいちばん右（列の並びは
            // 左・中央・右の順）のパネルだけが窓の右の辺に触れる
            var trailing = detail ? "Detail" : main ? "Main" : side ? "Side" : null;
            return pane == trailing ? innerRight : none;
        }

        // 右端に寄せていれば内側は左。開いている中でいちばん左のパネルだけが
        // 窓の左の辺に触れる
        var leading = side ? "Side" : main ? "Main" : detail ? "Detail" : null;
        return pane == leading ? innerLeft : none;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
