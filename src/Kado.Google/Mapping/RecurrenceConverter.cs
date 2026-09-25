using System.Globalization;

namespace Kado.Google.Mapping;

/// <summary>
/// 繰り返しの書き方を行き来する。
/// <para>
/// Google は RFC 5545 の行を配列で持つ（<c>["RRULE:FREQ=WEEKLY;BYDAY=MO",
/// "EXDATE;TZID=Asia/Tokyo:20260921T090000"]</c>）。こちらは1本の文字列で、
/// 除外日も同じ文字列に <c>EXDATE=</c> として混ぜている。
/// </para>
/// </summary>
public static class RecurrenceConverter
{
    /// <summary>
    /// Google の配列からこちらの1本に直す。
    /// <para>
    /// 表せない行（<c>RDATE</c> や <c>EXRULE</c>）があれば null を返す。
    /// 半端に読み取ると、書き戻しのときに落としてしまう。控えた生データは残るので、
    /// <c>patch</c> で <c>recurrence</c> を送らなければ Google 側は無傷。
    /// </para>
    /// </summary>
    public static string? FromGoogle(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0) return null;

        string? rule = null;
        var exceptDates = new List<string>();

        foreach (var line in lines)
        {
            var name = line.Split([';', ':'], 2)[0].Trim().ToUpperInvariant();

            switch (name)
            {
                case "RRULE":
                    // 2本目以降の RRULE は表せない
                    if (rule is not null) return null;
                    rule = After(line, ':');
                    break;

                case "EXDATE":
                    foreach (var value in (After(line, ':') ?? string.Empty)
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (DatePart(value) is { } date) exceptDates.Add(date);
                        else return null;
                    }

                    break;

                default:
                    // RDATE / EXRULE は Core が扱えない
                    return null;
            }
        }

        if (rule is null) return null;

        return exceptDates.Count > 0 ? $"{rule};EXDATE={string.Join(',', exceptDates)}" : rule;
    }

    /// <summary>
    /// こちらの1本を Google の配列に直す。
    /// <para>空や null なら空の配列。Google では空の配列が「繰り返しを外す」意味になる。</para>
    /// </summary>
    public static IReadOnlyList<string> ToGoogle(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return [];

        var body = spec.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) body = body["RRULE:".Length..];

        var ruleParts = new List<string>();
        var exceptDates = new List<string>();

        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("EXDATE=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in part["EXDATE=".Length..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (DatePart(value) is { } date) exceptDates.Add(date);
                }
            }
            else
            {
                ruleParts.Add(part);
            }
        }

        if (ruleParts.Count == 0) return [];

        var lines = new List<string> { $"RRULE:{string.Join(';', ruleParts)}" };
        if (exceptDates.Count > 0) lines.Add($"EXDATE;VALUE=DATE:{string.Join(',', exceptDates)}");

        return lines;
    }

    /// <summary>
    /// 繰り返しに除外日を足す。
    /// <para>
    /// Google で「この回だけ」を差し替えると、その回は親と例外回の両方に現れる。
    /// 親からその日を除かないと、同じ日に二重に出る。
    /// </para>
    /// <para>すでに除いてあれば何もしない。繰り返しでなければ触らない。</para>
    /// </summary>
    /// <returns>足したあとの指定。変わらなければ元のまま。</returns>
    public static string? WithExceptionDate(string? spec, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(spec)) return spec;

        var stamp = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var ruleParts = new List<string>();
        var exceptDates = new List<string>();

        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("EXDATE=", StringComparison.OrdinalIgnoreCase))
            {
                exceptDates.AddRange(part["EXDATE=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                ruleParts.Add(part);
            }
        }

        if (exceptDates.Contains(stamp, StringComparer.OrdinalIgnoreCase)) return spec;

        exceptDates.Add(stamp);
        exceptDates.Sort(StringComparer.Ordinal);

        return $"{string.Join(';', ruleParts)};EXDATE={string.Join(',', exceptDates)}";
    }

    /// <summary>
    /// 除外日（<c>EXDATE=</c>）を取り除いた指定。
    /// <para>
    /// <c>EXDATE</c> は編集画面の選択肢には無く、同期の取り込み処理が「同じ日に親の回と
    /// 例外回が二重に出ない」ようにするためだけに内部で書き足す。
    /// Google 側の親イベントは、繰り返しのうち1回を差し替えても <c>recurrence</c> に
    /// <c>EXDATE</c> を持たない（別のイベントとして持つ）。書き戻すときにこちらの内部事情を
    /// 混ぜて送り返すと、Google 側には要らない変更として PATCH してしまう。
    /// </para>
    /// </summary>
    public static string? WithoutExceptionDates(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return spec;

        var ruleParts = spec
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("EXDATE=", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return ruleParts.Length > 0 ? string.Join(';', ruleParts) : null;
    }

    /// <summary>
    /// 書き戻す recurrence の行を組み立てる。
    /// <para>
    /// 使う人が RRULE そのものを変えていなければ、Google 側にある元の行
    /// （<paramref name="originalLines"/>）を、<b>EXDATE を含め書式も並びもそのまま</b>
    /// 返す。ics 取り込みや他のクライアントが付けた本物の EXDATE は、こちらが
    /// 経由すると <c>yyyyMMdd</c> に丸めて書式（TZID など）を失うため、こちらの表現へ
    /// 一度変換してから戻すのではなく、元の文字列をそのまま使う。
    /// </para>
    /// <para>
    /// RRULE を変えていれば、新しい RRULE と、元にあった EXDATE 行だけを組み合わせる
    /// （やはり書式はそのまま）。どちらの場合も、<b>ローカルの EXDATE</b>
    /// （<see cref="WithExceptionDate"/> が内部で足した、同じ日の二重表示を防ぐためだけの
    /// 除外日）は使わない。Google の親はそもそも EXDATE を持たないか、持っていても
    /// こちらが動かしてよいものではない。
    /// </para>
    /// </summary>
    /// <param name="localSpec">こちらの1本（内部の除外日を含みうる）。</param>
    /// <param name="originalLines">
    /// Google 側の元の recurrence 行。新規作成でまだ無い、またはこの予定がまだ
    /// 繰り返しでなかったときは null。
    /// </param>
    public static IReadOnlyList<string> BuildOutgoing(string? localSpec, IReadOnlyList<string>? originalLines)
    {
        var ruleOnly = WithoutExceptionDates(localSpec);
        if (ruleOnly is null) return [];

        if (originalLines is { Count: > 0 })
        {
            var originalRuleLine = originalLines.FirstOrDefault(line => LineName(line) == "RRULE");
            var originalRuleText = originalRuleLine is not null ? After(originalRuleLine, ':') : null;

            // 変えていない。元の並び・書式をそのまま使う（本物の EXDATE も含め）。
            // こうすると書き戻す内容が控えた姿と一字一句一致し、送る必要も無くなる
            if (originalRuleText is not null && string.Equals(originalRuleText, ruleOnly, StringComparison.Ordinal))
            {
                return originalLines;
            }

            // RRULE を変えた。新しい RRULE と、元にあった EXDATE 行だけを組み合わせる
            var merged = new List<string> { $"RRULE:{ruleOnly}" };
            merged.AddRange(originalLines.Where(line => LineName(line) == "EXDATE"));
            return merged;
        }

        // Google 側にまだ recurrence が無い（新規作成、またはいま繰り返しにした）
        return [$"RRULE:{ruleOnly}"];
    }

    /// <summary>行の名前（<c>RRULE</c>・<c>EXDATE</c> など）。</summary>
    private static string LineName(string line) => line.Split([';', ':'], 2)[0].Trim().ToUpperInvariant();

    /// <summary>区切りの後ろ。無ければ null。</summary>
    private static string? After(string line, char separator)
    {
        var index = line.IndexOf(separator);
        return index >= 0 && index + 1 < line.Length ? line[(index + 1)..].Trim() : null;
    }

    /// <summary>
    /// 日付の部分だけを <c>yyyyMMdd</c> で取り出す。
    /// <para>
    /// <c>20260921T090000Z</c> のように時刻が付くことがある。こちらは日付単位でしか
    /// 除外を持てないので、日付に丸める。
    /// </para>
    /// </summary>
    private static string? DatePart(string value)
    {
        var text = value.Trim();
        if (text.Length >= 8 && DateOnly.TryParseExact(
                text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return text[..8];
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        return null;
    }
}
