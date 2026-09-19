using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlideinaCalendar.Google.Mapping;

/// <summary>
/// JSON の読み取りと正規化。
/// <para>
/// 受け取った姿を控えて差分の判定に使う（要件書 6.3）。キーの並び順は保証されないので、
/// <b>並べ替えてから比べる</b>。並び順の違いで「変わった」と誤判定すると、
/// 毎回の同期で書き戻しが走る。
/// </para>
/// </summary>
public static class GoogleJson
{
    /// <summary>キーを並べ替えた、比較用の文字列にする。</summary>
    public static string Normalize(JsonElement element) =>
        Sort(JsonNode.Parse(element.GetRawText())).ToJsonString();

    /// <summary>同じ内容か。キーの並び順は見ない。</summary>
    public static bool SameContent(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;

        try
        {
            return string.Equals(
                Sort(JsonNode.Parse(left)).ToJsonString(),
                Sort(JsonNode.Parse(right)).ToJsonString(),
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            // 壊れていたら「違う」とみなす。取り直せば直る
            return false;
        }
    }

    /// <summary>入れ子も含めてキーを並べ替える。</summary>
    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject source:
                var sorted = new JsonObject();
                foreach (var pair in source.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = Sort(pair.Value?.DeepClone());
                }

                return sorted;

            case JsonArray array:
                // 配列は順番そのものが意味を持つ（recurrence の行など）ので並べ替えない
                var items = new JsonArray();
                foreach (var item in array) items.Add(Sort(item?.DeepClone()));

                return items;

            default:
                return node?.DeepClone();
        }
    }

    // ------------------------------------------------------------------
    // 読み取りの補助。無い項目を null で受けられるようにする
    // ------------------------------------------------------------------

    /// <summary>文字列を読む。無いか空なら null。</summary>
    public static string? Text(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    /// <summary>真偽値を読む。無ければ既定値。</summary>
    public static bool Flag(this JsonElement element, string name, bool fallback = false) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    /// <summary>入れ子を読む。無ければ null。</summary>
    public static JsonElement? Child(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    /// <summary>配列を文字列として読む。無ければ空。</summary>
    public static IReadOnlyList<string> TextArray(this JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) result.Add(text);
        }

        return result;
    }

    /// <summary>RFC3339 の時刻を読む。無いか読めなければ null。</summary>
    public static DateTimeOffset? Timestamp(this JsonElement element, string name) =>
        element.Text(name) is { } text && DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
