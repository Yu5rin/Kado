namespace SlideinaCalendar.Core.WorkingDays;

/// <summary>
/// 日本の祝日。
/// <para>
/// 祝日法から<b>計算で出す</b>。データファイルも通信も要らないので、閉じた社内の PC でも
/// 正しく出る。内閣府の CSV を取りに行く案もあったが、置き場所と更新の手立てが要るうえ、
/// 繋がらない端末で空になる。
/// </para>
/// <para>
/// 祝日は非稼働日とは別物。実働日データは会社の稼働日であり、祝日でも稼働することがある。
/// ここが返すのは<b>休みの理由を読ませるための名前</b>にすぎない。
/// </para>
/// <para>
/// 対応するのは 2000年以降。春分・秋分の近似式が 2099年まで有効なので、そこが上限。
/// それより前は、当時の法が今と違う（ハッピーマンデーの導入前など）ので返さない。
/// </para>
/// </summary>
public static class JapaneseHolidays
{
    /// <summary>計算できる最初の年。</summary>
    public const int FirstYear = 2000;

    /// <summary>計算できる最後の年。春分・秋分の近似式の上限。</summary>
    public const int LastYear = 2099;

    /// <summary>その日の祝日名。祝日でなければ null。</summary>
    public static string? NameOf(DateOnly date) => Of(date.Year).GetValueOrDefault(date);

    /// <summary>その年の祝日をすべて返す。</summary>
    public static IReadOnlyDictionary<DateOnly, string> Of(int year)
    {
        if (year is < FirstYear or > LastYear) return new Dictionary<DateOnly, string>();

        var days = new Dictionary<DateOnly, string>();

        foreach (var (date, name) in Fixed(year)) days[date] = name;
        foreach (var (date, name) in HappyMonday(year)) days[date] = name;

        days[new DateOnly(year, 3, EquinoxDay(year, spring: true))] = "春分の日";
        days[new DateOnly(year, 9, EquinoxDay(year, spring: false))] = "秋分の日";

        foreach (var (date, name) in OneOff(year)) days[date] = name;

        AddSubstitutes(days, year);
        AddBridges(days, year);

        return days;
    }

    /// <summary>日付が決まっているもの。法が変わった年で切り替える。</summary>
    private static IEnumerable<(DateOnly Date, string Name)> Fixed(int year)
    {
        yield return (new DateOnly(year, 1, 1), "元日");
        yield return (new DateOnly(year, 2, 11), "建国記念の日");

        // 2020年から。前年までは 12月23日
        if (year >= 2020) yield return (new DateOnly(year, 2, 23), "天皇誕生日");
        else if (year <= 2018) yield return (new DateOnly(year, 12, 23), "天皇誕生日");

        yield return (new DateOnly(year, 4, 29), "昭和の日");
        yield return (new DateOnly(year, 5, 3), "憲法記念日");
        yield return (new DateOnly(year, 5, 4), "みどりの日");
        yield return (new DateOnly(year, 5, 5), "こどもの日");

        // 2016年から
        if (year >= 2016 && year != 2020 && year != 2021)
        {
            yield return (new DateOnly(year, 8, 11), "山の日");
        }

        yield return (new DateOnly(year, 11, 3), "文化の日");
        yield return (new DateOnly(year, 11, 23), "勤労感謝の日");
    }

    /// <summary>月の第 n 月曜に置くもの。</summary>
    private static IEnumerable<(DateOnly Date, string Name)> HappyMonday(int year)
    {
        yield return (NthMonday(year, 1, 2), "成人の日");

        // 海の日・スポーツの日は、2020年と2021年だけ五輪に合わせて動いた
        if (year != 2020 && year != 2021) yield return (NthMonday(year, 7, 3), "海の日");

        yield return (NthMonday(year, 9, 3), "敬老の日");

        if (year != 2020 && year != 2021)
        {
            yield return (NthMonday(year, 10, 2), year >= 2020 ? "スポーツの日" : "体育の日");
        }
    }

    /// <summary>その年だけのもの。即位と五輪で動いた分。</summary>
    private static IEnumerable<(DateOnly Date, string Name)> OneOff(int year)
    {
        switch (year)
        {
            case 2019:
                yield return (new DateOnly(2019, 5, 1), "天皇の即位の日");
                yield return (new DateOnly(2019, 10, 22), "即位礼正殿の儀の行われる日");
                break;

            case 2020:
                yield return (new DateOnly(2020, 7, 23), "海の日");
                yield return (new DateOnly(2020, 7, 24), "スポーツの日");
                yield return (new DateOnly(2020, 8, 10), "山の日");
                break;

            case 2021:
                yield return (new DateOnly(2021, 7, 22), "海の日");
                yield return (new DateOnly(2021, 7, 23), "スポーツの日");
                yield return (new DateOnly(2021, 8, 8), "山の日");
                break;
        }
    }

    /// <summary>
    /// 振替休日。祝日が日曜に当たったら、次の祝日でない日を休みにする。
    /// </summary>
    private static void AddSubstitutes(Dictionary<DateOnly, string> days, int year)
    {
        foreach (var date in days.Keys.Where(d => d.DayOfWeek == DayOfWeek.Sunday).Order().ToArray())
        {
            var next = date.AddDays(1);
            while (days.ContainsKey(next)) next = next.AddDays(1);

            // 年をまたぐ分は翌年の計算で出る
            if (next.Year == year) days[next] = "振替休日";
        }
    }

    /// <summary>
    /// 国民の休日。祝日に挟まれた平日を休みにする。
    /// <para>9月に敬老の日と秋分の日が1日空けて並ぶ年に出る。</para>
    /// </summary>
    private static void AddBridges(Dictionary<DateOnly, string> days, int year)
    {
        foreach (var date in days.Keys.Order().ToArray())
        {
            var between = date.AddDays(1);
            if (between.Year != year || days.ContainsKey(between)) continue;
            if (between.DayOfWeek == DayOfWeek.Sunday) continue;
            if (!days.ContainsKey(between.AddDays(1))) continue;

            days[between] = "国民の休日";
        }
    }

    private static DateOnly NthMonday(int year, int month, int nth)
    {
        var first = new DateOnly(year, month, 1);
        var shift = ((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7;

        return first.AddDays(shift + ((nth - 1) * 7));
    }

    /// <summary>
    /// 春分・秋分の日。
    /// <para>
    /// 天文の計算そのものではなく、官報に合わせた近似式。2099年まで一致する。
    /// 正確な値は毎年2月に官報で公表されるので、遠い将来の分は目安にすぎない。
    /// </para>
    /// </summary>
    private static int EquinoxDay(int year, bool spring)
    {
        var baseValue = spring ? 20.8431 : 23.2488;

        return (int)(baseValue + (0.242194 * (year - 1980)) - ((year - 1980) / 4));
    }
}
