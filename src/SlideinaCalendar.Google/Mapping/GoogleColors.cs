using System.Globalization;
using System.Text.Json;

namespace SlideinaCalendar.Google.Mapping;

/// <summary>
/// Google が用意している色の一覧。
/// <para>
/// Google のカレンダーとイベントは、任意の色ではなく<b>決まった番号（<c>colorId</c>）</b>で
/// 色を持つ。番号と実際の色の対応は <c>colors.get</c> が返すので、こちらで決め打ちしない。
/// 対応表を書き写すと、Google 側が色を調整したときに食い違う。
/// </para>
/// <para>
/// こちらは任意の <c>#rrggbb</c> を持てるので、書き戻すときは<b>いちばん近い番号</b>に寄せる。
/// 寄せた結果は次の同期で降ってくるので、画面の色もそれに揃う。
/// </para>
/// </summary>
public sealed class GoogleColors
{
    private readonly IReadOnlyDictionary<string, string> _calendar;
    private readonly IReadOnlyDictionary<string, string> _event;

    private GoogleColors(
        IReadOnlyDictionary<string, string> calendar, IReadOnlyDictionary<string, string> events)
    {
        _calendar = calendar;
        _event = events;
    }

    /// <summary>カレンダー用の色（番号 → <c>#rrggbb</c>）。</summary>
    public IReadOnlyDictionary<string, string> Calendar => _calendar;

    /// <summary>予定用の色（番号 → <c>#rrggbb</c>）。</summary>
    public IReadOnlyDictionary<string, string> Event => _event;

    /// <summary>1つも読めなかったか。読めていなければ色の書き戻しはしない。</summary>
    public bool IsEmpty => _calendar.Count == 0 && _event.Count == 0;

    /// <summary><c>colors.get</c> の応答を読む。</summary>
    public static GoogleColors Read(JsonElement root) =>
        new(ReadSection(root, "calendar"), ReadSection(root, "event"));

    /// <summary>何も無い一覧。取れなかったときに使う。</summary>
    public static GoogleColors Empty { get; } =
        new(new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>
    /// この色にいちばん近いカレンダーの色番号。
    /// <para>一覧が空か、色を読めなければ null。</para>
    /// </summary>
    public string? ClosestCalendarId(string? color) => Closest(_calendar, color);

    /// <summary>この色にいちばん近い予定の色番号。</summary>
    public string? ClosestEventId(string? color) => Closest(_event, color);

    /// <summary>その番号の色。無ければ null。</summary>
    public string? CalendarColor(string? id) =>
        id is { Length: > 0 } key && _calendar.TryGetValue(key, out var value) ? value : null;

    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string> ReadSection(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.Text("background") is { } background)
            {
                result[entry.Name] = background;
            }
        }

        return result;
    }

    private static string? Closest(IReadOnlyDictionary<string, string> table, string? color)
    {
        if (table.Count == 0 || Parse(color) is not { } target) return null;

        string? best = null;
        var bestDistance = double.MaxValue;

        foreach (var pair in table)
        {
            if (Parse(pair.Value) is not { } candidate) continue;

            var distance = Distance(target, candidate);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = pair.Key;
        }

        return best;
    }

    /// <summary>
    /// 色の近さ。
    /// <para>
    /// 単純な RGB の差ではなく、目の感じ方に合わせて緑を重く、青を軽く見る。
    /// そのままだと、人が見て遠い色が「近い」と判定されることがある。
    /// </para>
    /// </summary>
    private static double Distance((int R, int G, int B) left, (int R, int G, int B) right)
    {
        double dr = left.R - right.R;
        double dg = left.G - right.G;
        double db = left.B - right.B;

        return (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
    }

    /// <summary><c>#rrggbb</c> を読む。読めなければ null。</summary>
    private static (int R, int G, int B)? Parse(string? color)
    {
        if (color is not { Length: > 0 }) return null;

        var text = color.TrimStart('#');
        if (text.Length != 6) return null;

        return int.TryParse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
               int.TryParse(text[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
               int.TryParse(text[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
            ? (r, g, b)
            : null;
    }
}
