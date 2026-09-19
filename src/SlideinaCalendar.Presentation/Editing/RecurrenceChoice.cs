using System.Globalization;
using SlideinaCalendar.Core.Recurrence;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>編集画面で選べる繰り返しの種類。</summary>
public enum RecurrenceKind
{
    /// <summary>繰り返さない。</summary>
    None,

    Daily,
    Weekly,
    Monthly,
    Yearly,

    /// <summary>
    /// 画面の選択肢では表せない指定。
    /// <para>
    /// 取り込んだ予定が「隔週の月水金」のような指定を持っていることがある。
    /// 選択肢に無いからと消すと、保存し直すたびに繰り返しが失われる。
    /// </para>
    /// </summary>
    Custom,
}

/// <summary>繰り返しの選択肢1つ。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Label">「毎週 木曜日」のような表示。開始日によって変わる。</param>
public sealed record RecurrenceOption(RecurrenceKind Kind, string Label)
{
    /// <summary>
    /// 表示名をそのまま返す。
    /// <para>
    /// 選択中の項目をどう描くかはコントロールのテンプレート次第で、レコードの
    /// 既定の文字列表現（<c>RecurrenceOption { Kind = ... }</c>）が出てしまうことがある。
    /// </para>
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// 繰り返しの指定文字列（RRULE）と、画面の選択肢を行き来する。
/// <para>
/// Google Calendar の <c>recurrence</c> はこの形で入る。よく使う4つだけを選択肢にし、
/// それ以外は <see cref="RecurrenceKind.Custom"/> として元の文字列をそのまま持ち続ける。
/// </para>
/// </summary>
public static class RecurrenceChoice
{
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static readonly string[] DayNames = ["日", "月", "火", "水", "木", "金", "土"];

    /// <summary>開始日に合わせた選択肢。<paramref name="includeCustom"/> は元が独自指定のときだけ true。</summary>
    public static IReadOnlyList<RecurrenceOption> OptionsFor(DateOnly date, bool includeCustom)
    {
        var options = new List<RecurrenceOption>(6)
        {
            new(RecurrenceKind.None, "繰り返さない"),
            new(RecurrenceKind.Daily, "毎日"),
            new(RecurrenceKind.Weekly, $"毎週 {DayNames[(int)date.DayOfWeek]}曜日"),
            new(RecurrenceKind.Monthly, $"毎月 {date.Day}日"),
            new(RecurrenceKind.Yearly, $"毎年 {date.ToString("M月d日", CultureInfo.InvariantCulture)}"),
        };

        if (includeCustom) options.Add(new RecurrenceOption(RecurrenceKind.Custom, "この予定の設定のまま"));

        return options;
    }

    /// <summary>種類から指定文字列を作る。繰り返さないなら null。</summary>
    public static string? ToSpec(RecurrenceKind kind, DateOnly date) => kind switch
    {
        RecurrenceKind.Daily => "FREQ=DAILY",
        RecurrenceKind.Weekly => $"FREQ=WEEKLY;BYDAY={DayCodes[(int)date.DayOfWeek]}",
        RecurrenceKind.Monthly => $"FREQ=MONTHLY;BYMONTHDAY={date.Day}",
        RecurrenceKind.Yearly => $"FREQ=YEARLY;BYMONTH={date.Month};BYMONTHDAY={date.Day}",
        _ => null,
    };

    /// <summary>
    /// 指定文字列から種類を読む。
    /// <para>
    /// 画面で作れる形と一致するかどうかだけを見る。一致しなければ
    /// <see cref="RecurrenceKind.Custom"/> とし、元の文字列は呼び出し側が持ち続ける。
    /// </para>
    /// </summary>
    public static RecurrenceKind KindOf(string? spec, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(spec)) return RecurrenceKind.None;

        // 書式が壊れているものを「繰り返さない」に倒すと、保存し直したときに消える
        if (!TryNormalize(spec, out var normalized)) return RecurrenceKind.Custom;

        foreach (var kind in (RecurrenceKind[])[RecurrenceKind.Daily, RecurrenceKind.Weekly,
                                                RecurrenceKind.Monthly, RecurrenceKind.Yearly])
        {
            if (ToSpec(kind, date) is { } candidate &&
                TryNormalize(candidate, out var expected) &&
                string.Equals(normalized, expected, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return RecurrenceKind.Custom;
    }

    /// <summary>
    /// 並び順や大文字小文字の違いを均す。
    /// <para><c>RRULE:</c> の接頭辞も落とす。Google の配列はこれを付けて返す。</para>
    /// </summary>
    private static bool TryNormalize(string spec, out string normalized)
    {
        normalized = string.Empty;

        var body = spec.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) body = body["RRULE:".Length..];

        try
        {
            // 読めない指定を独自に解釈しないよう、Core の実装に通してから比べる
            _ = RecurrenceRule.Parse(body);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }

        normalized = string.Join(';', body
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant())
            .Order(StringComparer.Ordinal));

        return true;
    }
}
