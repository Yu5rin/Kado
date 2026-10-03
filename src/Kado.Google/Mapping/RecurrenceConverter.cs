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
    /// <paramref name="source"/> が持つ除外日（<c>EXDATE=</c>）を、<paramref name="rule"/> に付ける。
    /// <para>
    /// 編集画面が繰り返しを選び直す（曜日の追従など）とき、手元で足した除外日を失わないために使う。
    /// 除外日が無ければ <paramref name="rule"/> のまま。
    /// </para>
    /// </summary>
    public static string? CopyExceptionDates(string? rule, string? source)
    {
        if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrWhiteSpace(source)) return rule;

        var part = source.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith("EXDATE=", StringComparison.OrdinalIgnoreCase));

        return part is null ? rule : $"{rule};{part}";
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
    /// <param name="startChange">
    /// 系列の開始時刻を変えて送るときの、前後の開始時刻。<b>変えていなければ null</b> で、
    /// そのときは元の行を一字一句そのまま使う。変えているときは、時刻つきの EXDATE の
    /// 時刻部分を新しい開始時刻に合わせて書き直す（<see cref="RetimeExceptionLines"/>）。
    /// </param>
    public static IReadOnlyList<string> BuildOutgoing(
        string? localSpec, IReadOnlyList<string>? originalLines, SeriesStartChange? startChange = null)
    {
        var ruleOnly = WithoutExceptionDates(localSpec);
        if (ruleOnly is null) return [];

        if (originalLines is { Count: > 0 })
        {
            var originalRuleLine = originalLines.FirstOrDefault(line => LineName(line) == "RRULE");
            var originalRuleText = originalRuleLine is not null ? After(originalRuleLine, ':') : null;

            // 変えていない。元の並び・書式をそのまま使う（本物の EXDATE も含め）。
            // こうすると書き戻す内容が控えた姿と一字一句一致し、送る必要も無くなる。
            // ただし開始時刻を変えたときは、時刻つきの EXDATE が新しい回と合わなくなるので直す
            if (originalRuleText is not null && string.Equals(originalRuleText, ruleOnly, StringComparison.Ordinal))
            {
                return startChange is { } change ? RetimeExceptionLines(originalLines, change) : originalLines;
            }

            // RRULE を変えた。新しい RRULE と、元にあった EXDATE 行だけを組み合わせる
            var merged = new List<string> { $"RRULE:{ruleOnly}" };
            merged.AddRange(originalLines.Where(line => LineName(line) == "EXDATE"));

            return startChange is { } moved ? RetimeExceptionLines(merged, moved) : merged;
        }

        // Google 側にまだ recurrence が無い（新規作成、またはいま繰り返しにした）
        return [$"RRULE:{ruleOnly}"];
    }

    /// <summary>系列の開始時刻の、変える前と後。</summary>
    /// <param name="Before">最後に Google から受け取った開始（時差つき）。</param>
    /// <param name="After">これから送る開始（時差つき）。</param>
    public readonly record struct SeriesStartChange(DateTimeOffset Before, DateTimeOffset After);

    /// <summary>
    /// 時刻つきの EXDATE の時刻部分を、新しい開始時刻に合わせて書き直す。
    /// <para>
    /// 系列の開始時刻を変えると、元の <c>EXDATE;TZID=…:20261005T090000</c> がそのままでは
    /// 新しい回（10:00 の回）と合わず、除外が外れて中止した回が復活する。<b>日付はそのまま</b>、
    /// 時刻だけを新しい開始時刻（その EXDATE の時差で見た時刻）にする。TZID と書式（区切り・
    /// 末尾の <c>Z</c>）は保つ。終日の EXDATE（<c>VALUE=DATE</c>・日付だけ）は触らない。
    /// その時差で見た開始時刻が前後で同じなら、行は一字一句そのまま。
    /// </para>
    /// </summary>
    private static List<string> RetimeExceptionLines(IReadOnlyList<string> lines, SeriesStartChange change) =>
        lines.Select(line => LineName(line) == "EXDATE" ? RetimeExceptionLine(line, change) : line).ToList();

    private static string RetimeExceptionLine(string line, SeriesStartChange change)
    {
        var colon = line.IndexOf(':');
        if (colon < 0 || colon + 1 >= line.Length) return line;

        var head = line[..colon];
        var values = line[(colon + 1)..];

        // 終日の指定は触らない
        if (head.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) &&
            !head.Contains("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        TimeZoneInfo? zone = null;
        var tzid = head.Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase));
        if (tzid is not null) zone = FindZone(tzid["TZID=".Length..].Trim('"'));

        var changed = false;

        var rewritten = values.Split(',').Select(raw =>
        {
            var value = raw.Trim();

            // 日付だけの値は触らない
            if (value.Length < 15 || value[8] != 'T') return raw;

            var isUtc = value.EndsWith('Z');
            // その値が置かれている時差で見た、開始時刻の前後
            TimeSpan old, now;
            if (isUtc)
            {
                (old, now) = (change.Before.UtcDateTime.TimeOfDay, change.After.UtcDateTime.TimeOfDay);
            }
            else if (zone is not null)
            {
                (old, now) = (TimeZoneInfo.ConvertTime(change.Before, zone).TimeOfDay,
                              TimeZoneInfo.ConvertTime(change.After, zone).TimeOfDay);
            }
            else
            {
                // 時差の指定が無い（または読めない）値は、開始が持つ時差のままの時刻と見る
                (old, now) = (change.Before.TimeOfDay, change.After.TimeOfDay);
            }

            // 開始時刻（時・分・秒）が変わっていなければ、そのまま
            if (old == now) return raw;

            changed = true;

            return value[..9] + $"{now.Hours:D2}{now.Minutes:D2}{now.Seconds:D2}" + (isUtc ? "Z" : string.Empty);
        }).ToArray();

        return changed ? $"{head}:{string.Join(',', rewritten)}" : line;
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException
                                       or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 受け取った指定に、手元だけで足した除外日を引き継ぐ。
    /// <para>
    /// 例外回（動かした回・中止した回）のために、取り込みは親の指定へ <c>EXDATE=</c> を足す。
    /// Google の親は EXDATE を持たないので、親を送った応答や取り込み直した姿にはそれが無く、
    /// そのまま置き換えると除外が消える。<b>引き継ぐのは、前に Google から受け取った行に
    /// 無かった除外日</b>（<paramref name="previousGoogleLines"/> と比べて決める）だけ。
    /// Google 側が外した除外日までは残さない。
    /// </para>
    /// </summary>
    /// <param name="incoming">いま届いた姿から読んだ指定。null（外れている・表せない）なら何もしない。</param>
    /// <param name="previousLocal">手元にあった指定（手元で足した除外日を含みうる）。</param>
    /// <param name="previousGoogleLines">前に Google から受け取った recurrence 行。</param>
    public static string? KeepLocalExceptionDates(
        string? incoming, string? previousLocal, IReadOnlyList<string>? previousGoogleLines)
    {
        if (string.IsNullOrWhiteSpace(incoming) || string.IsNullOrWhiteSpace(previousLocal)) return incoming;

        var known = ExceptionDatesOf(previousGoogleLines is { Count: > 0 } ? FromGoogle(previousGoogleLines) : null);
        var current = ExceptionDatesOf(incoming);

        var result = incoming;

        foreach (var date in ExceptionDatesOf(previousLocal))
        {
            if (known.Contains(date) || current.Contains(date)) continue;

            result = WithExceptionDate(result, date);
        }

        return result;
    }

    /// <summary>指定に含まれる除外日（<c>EXDATE=</c>）の一覧。</summary>
    private static HashSet<DateOnly> ExceptionDatesOf(string? spec)
    {
        var result = new HashSet<DateOnly>();
        if (string.IsNullOrWhiteSpace(spec)) return result;

        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("EXDATE=", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var value in part["EXDATE=".Length..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (DatePart(value) is { } text && DateOnly.TryParseExact(
                        text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    result.Add(date);
                }
            }
        }

        return result;
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
