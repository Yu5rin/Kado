using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

/// <summary>
/// テスト用の実働日データ。
/// <para>
/// 基準は <b>2026年9月</b>。要件書 4.4 の例「残り 2実働日（9/25まで）」がそのまま再現できる
/// 並びなので、仕様例を直接テストに落とせる。
/// </para>
/// <code>
///  日  月  火  水  木  金  土
///           1   2   3   4   5×
///   6×  7   8   9  10  11  12×
///  13× 14  15  16  17  18  19×
///  20× 21休 22休 23休 24  25  26×
///  27× 28  29  30
/// </code>
/// <para>× は土日、休 は祝日（敬老の日・国民の休日・秋分の日）。いずれも非稼働日。</para>
/// </summary>
internal static class TestCalendars
{
    /// <summary>2026年9月の祝日。</summary>
    private static readonly DateOnly[] Holidays2026 =
    [
        new(2026, 9, 21),   // 敬老の日
        new(2026, 9, 22),   // 国民の休日
        new(2026, 9, 23),   // 秋分の日
    ];

    /// <summary>2026年9月の稼働日（19日ぶん）。</summary>
    public static IReadOnlyList<DateOnly> September2026WorkingDays { get; } = Enumerable
        .Range(1, 30)
        .Select(d => new DateOnly(2026, 9, d))
        .Where(IsWorkday)
        .ToArray();

    /// <summary>
    /// 2026年9月だけを登録したカレンダー。範囲は 9/1〜9/30 ちょうど。
    /// 範囲外へ出るケースを試しやすいので、既定のテスト対象にしている。
    /// </summary>
    public static WorkingDayCalendar September2026 { get; } = WorkingDayCalendar.Create(
        September2026WorkingDays,
        new DateOnly(2026, 9, 1),
        new DateOnly(2026, 9, 30),
        [
            new Milestone(new DateOnly(2026, 9, 14), "仕様期限", "Ver．26.1"),
            new Milestone(new DateOnly(2026, 9, 15), "1次GO", "Ver．26.1"),
            new Milestone(new DateOnly(2026, 9, 16), "S中日程", "Ver．26.1"),
            new Milestone(new DateOnly(2026, 9, 17), "M中日程", "Ver．26.1"),
        ],
        new DateOnly(2026, 9, 14),
        new DateOnly(2026, 9, 17));

    /// <summary>2026年9月〜12月を登録したカレンダー（9月のみ祝日を除く）。月をまたぐ計算用。</summary>
    public static WorkingDayCalendar AutumnToYearEnd2026 { get; } = WorkingDayCalendar.Create(
        EnumerateDays(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)).Where(IsWorkday),
        new DateOnly(2026, 9, 1),
        new DateOnly(2026, 12, 31),
        null, null, null);

    public static WorkingDayMath MathFor(WorkingDayCalendar calendar) => new(calendar);

    public static WorkingDayMath September2026Math { get; } = new(September2026);

    public static DueDateFormatter September2026Formatter { get; } = new(September2026Math);

    private static bool IsWorkday(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !Holidays2026.Contains(d);

    private static IEnumerable<DateOnly> EnumerateDays(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }
}
