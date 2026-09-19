using System.Globalization;

namespace SlideinaCalendar.Core.Recurrence;

/// <summary>
/// 繰り返し指定文字列をキー・値に分解しただけの入れ物。
/// パターン実装はここから必要な値だけを読む。
/// </summary>
public sealed class RecurrenceParameters
{
    private readonly IReadOnlyDictionary<string, string> _values;

    internal RecurrenceParameters(IReadOnlyDictionary<string, string> values) => _values = values;

    /// <summary>生の値を取り出す。キーが無ければ null。</summary>
    public string? Get(string key) =>
        _values.TryGetValue(key.ToUpperInvariant(), out var v) ? v : null;

    /// <summary>キーが含まれているか。</summary>
    public bool Has(string key) => _values.ContainsKey(key.ToUpperInvariant());

    /// <summary>正の整数として取り出す。未指定・不正なら <paramref name="fallback"/>。</summary>
    public int GetPositiveInt(string key, int fallback)
    {
        var raw = Get(key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : fallback;
    }

    /// <summary>カンマ区切りの整数リストとして取り出す。未指定なら空。</summary>
    public IReadOnlyList<int> GetIntList(string key)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var result = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                result.Add(v);
            }
            else
            {
                throw new FormatException($"{key} に整数でない値が含まれています: '{part}'");
            }
        }
        return result;
    }

    /// <summary>カンマ区切りの曜日リスト（MO/TU/...）として取り出す。未指定なら空。</summary>
    public IReadOnlyList<DayOfWeek> GetDayList(string key)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var result = new List<DayOfWeek>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(RecurrenceCodes.ParseDay(part));
        }
        return result;
    }
}
