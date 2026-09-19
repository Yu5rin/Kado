using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
