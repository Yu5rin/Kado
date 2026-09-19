using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;

namespace SlideinaCalendar.Core.Recurrence;

/// <summary>
/// 繰り返しルールの構築と判定。
/// <para>
/// 指定文字列は RFC 5545 の RRULE のサブセットを採る。独自形式にしないのは、
/// Phase 4 の Google Calendar 同期でそのまま受け渡せるようにするため。
/// </para>
/// <code>
/// FREQ=DAILY
/// FREQ=DAILY;INTERVAL=2
/// FREQ=WEEKLY;BYDAY=TU
/// FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR
/// FREQ=MONTHLY;BYMONTHDAY=15
/// FREQ=MONTHLY;BYMONTHDAY=-1          … 月末
/// FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=19
/// FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231 … 終了日つき
/// FREQ=YEARLY;EXDATE=20260421,20270421 … 除外日つき
/// </code>
/// <para>
/// 現時点の対応は 毎日／毎週（曜日指定）／毎月（日付指定）／毎年 と、
/// 終了日（UNTIL）、除外日（EXDATE）。種別は <see cref="RegisterPattern"/> で後から足せる。
/// </para>
/// </summary>
public sealed class RecurrenceRule
{
    private static readonly ConcurrentDictionary<string, Func<RecurrenceParameters, IRecurrencePattern>> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DAILY"] = BuiltInPatterns.CreateDaily,
            ["WEEKLY"] = BuiltInPatterns.CreateWeekly,
            ["MONTHLY"] = BuiltInPatterns.CreateMonthly,
            ["YEARLY"] = BuiltInPatterns.CreateYearly,
        };

    private RecurrenceRule(IRecurrencePattern pattern, DateOnly? until, IReadOnlySet<DateOnly> exceptDates)
    {
        Pattern = pattern;
        Until = until;
        ExceptDates = exceptDates;
    }

    /// <summary>周期の判定ロジック。</summary>
    public IRecurrencePattern Pattern { get; }

    /// <summary>終了日（この日までは繰り返す）。無期限なら null。</summary>
    public DateOnly? Until { get; }

    /// <summary>
    /// 繰り返しから除外する日。
    /// <para>
    /// 「毎年この日」と決めたあとで、特定の年だけ取りやめるようなときに使う。
    /// RFC 5545 の EXDATE にあたる（本来は RRULE とは別行だが、ここでは同じ指定文字列に含める）。
    /// </para>
    /// </summary>
    public IReadOnlySet<DateOnly> ExceptDates { get; }

    /// <summary>種別名（<c>DAILY</c> / <c>WEEKLY</c> / <c>MONTHLY</c> / <c>YEARLY</c> …）。</summary>
    public string Frequency => Pattern.Frequency;

    /// <summary>何回ごとか。</summary>
    public int Interval => Pattern.Interval;

    // ----------------------------------------------------------------------
    // 構築
    // ----------------------------------------------------------------------

    /// <summary>
    /// 指定文字列からルールを作る。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="spec"/> が空。</exception>
    /// <exception cref="FormatException">書式が不正、または未対応の種別。</exception>
    public static RecurrenceRule Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException("繰り返し指定が空です。", nameof(spec));
        }

        // Google Calendar の recurrence 配列は "RRULE:" 接頭辞つきで来るので受け入れる
        var body = spec.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            body = body["RRULE:".Length..];
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                throw new FormatException($"'キー=値' の形式になっていません: '{part}'");
            }
            var key = part[..eq].Trim().ToUpperInvariant();
            var value = part[(eq + 1)..].Trim();
            values[key] = value;
        }

        if (!values.TryGetValue("FREQ", out var freq) || string.IsNullOrWhiteSpace(freq))
        {
            throw new FormatException("FREQ が指定されていません。");
        }

        if (!Factories.TryGetValue(freq, out var factory))
        {
            throw new FormatException(
                $"未対応の繰り返し種別です: '{freq}'（対応: {string.Join(", ", Factories.Keys.Order())}）");
        }

        var parameters = new RecurrenceParameters(values);
        var pattern = factory(parameters);
        var until = ParseUntil(parameters.Get("UNTIL"));
        var except = ParseExceptDates(parameters.Get("EXDATE"));

        return new RecurrenceRule(pattern, until, except);
    }

    /// <summary>パース失敗を例外にせず判定したい場合。</summary>
    public static bool TryParse(string? spec, out RecurrenceRule? rule)
    {
        try
        {
            rule = spec is null ? null : Parse(spec);
            return rule is not null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            rule = null;
            return false;
        }
    }

    /// <summary>パターンから直接組み立てる。</summary>
    public static RecurrenceRule FromPattern(
        IRecurrencePattern pattern,
        DateOnly? until = null,
        IEnumerable<DateOnly>? exceptDates = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new RecurrenceRule(pattern, until, ToSet(exceptDates));
    }

    private static IReadOnlySet<DateOnly> ToSet(IEnumerable<DateOnly>? dates) =>
        dates is null ? FrozenSet<DateOnly>.Empty : dates.ToFrozenSet();

    /// <summary>
    /// 繰り返し種別を追加する。既存の種別に手を入れずに拡張できるようにするための口。
    /// </summary>
    /// <param name="frequency"><c>FREQ=</c> に書く種別名。</param>
    /// <param name="factory">パース済みパラメータからパターンを作る関数。</param>
    public static void RegisterPattern(string frequency, Func<RecurrenceParameters, IRecurrencePattern> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frequency);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[frequency] = factory;
    }

    /// <summary>現在対応している種別名の一覧。</summary>
    public static IReadOnlyCollection<string> SupportedFrequencies => Factories.Keys.Order().ToArray();

    // ----------------------------------------------------------------------
    // 判定・表示
    // ----------------------------------------------------------------------

    /// <summary>
    /// <paramref name="date"/> がこの繰り返しに該当するか。
    /// <para>
    /// 開始日そのものは常に該当する。開始日より前と、UNTIL を過ぎた日は該当しない。
    /// </para>
    /// </summary>
    public bool Matches(DateOnly date, DateOnly seriesStart)
    {
        if (date < seriesStart) return false;
        if (Until is { } until && date > until) return false;
        if (ExceptDates.Contains(date)) return false;
        return Pattern.Matches(date, seriesStart);
    }

    /// <summary>
    /// 期間内の該当日を列挙する。開始日から順に、<paramref name="to"/> まで。
    /// </summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly seriesStart, DateOnly from, DateOnly to)
    {
        var start = from < seriesStart ? seriesStart : from;
        var end = Until is { } u && u < to ? u : to;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (Matches(d, seriesStart)) yield return d;
        }
    }

    /// <summary>「毎週 火曜」などの表示用文字列。</summary>
    public string ToLabel() => ToLabel(null);

    /// <summary>
    /// 表示用文字列。曜日や日付を省略した指定でも、開始日を渡せば具体的に表示できる。
    /// </summary>
    public string ToLabel(DateOnly? seriesStart)
    {
        var label = Pattern.ToLabel(seriesStart);
        return Until is { } until
            ? $"{label}（{until.ToString("yyyy/M/d", CultureInfo.InvariantCulture)}まで）"
            : label;
    }

    /// <summary>指定文字列に戻す。保存・Google への受け渡しに使う。</summary>
    public string ToSpec()
    {
        var parts = new List<string> { Pattern.ToSpec() };

        if (Until is { } until)
        {
            parts.Add($"UNTIL={until.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
        }

        if (ExceptDates.Count > 0)
        {
            var dates = ExceptDates.Order().Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            parts.Add("EXDATE=" + string.Join(",", dates));
        }

        return string.Join(";", parts);
    }

    public override string ToString() => ToSpec();

    /// <summary>EXDATE をカンマ区切りで読む。空なら空集合。</summary>
    private static IReadOnlySet<DateOnly> ParseExceptDates(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FrozenSet<DateOnly>.Empty;

        var result = new HashSet<DateOnly>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(ParseDate(part, "EXDATE"));
        }
        return result.ToFrozenSet();
    }

    private static DateOnly? ParseUntil(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return ParseDate(raw, "UNTIL");
    }

    /// <summary>
    /// 日付を読む。RFC 5545 の <c>yyyyMMdd</c> を基本とし、UTC の日時形式
    /// （<c>20261231T145959Z</c>）と手書きの <c>2026-12-31</c> も受け入れる。
    /// </summary>
    private static DateOnly ParseDate(string raw, string field)
    {
        var datePart = raw.Length >= 8 ? raw[..8] : raw;

        if (DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var value))
        {
            return value;
        }

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value))
        {
            return value;
        }

        throw new FormatException($"{field} の日付が不正です: '{raw}'（yyyyMMdd 形式）");
    }
}
