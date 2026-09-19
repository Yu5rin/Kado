using System.Globalization;
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
