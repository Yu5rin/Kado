using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Import;

/// <summary>
/// 配布 Excel（ツール用実働日.xlsx）の取り込み結果。
/// <para>
/// 稼働日とマイルストーンで対象期間が異なるため、範囲をそれぞれ別に持つ。既存データへの反映は
/// <see cref="WorkingDayCalendar.Merge"/> がこの範囲を見て「期間だけ置き換え」を行う。
/// </para>
/// </summary>
/// <param name="Version">ヘッダ（1〜3行目）から読んだバージョン文字列（例 <c>Ver．25.1</c>）。見つからなければ空文字。</param>
/// <param name="WorkingDayRangeStart">稼働日の対象期間の開始日。</param>
/// <param name="WorkingDayRangeEnd">稼働日の対象期間の終了日。</param>
/// <param name="MilestoneRangeStart">マイルストーンの対象期間の開始日。1件も無ければ null。</param>
/// <param name="MilestoneRangeEnd">マイルストーンの対象期間の終了日。1件も無ければ null。</param>
/// <param name="WorkingDays">読み取った稼働日（昇順・重複なし）。</param>
/// <param name="Milestones">読み取ったマイルストーン（日付昇順）。</param>
/// <param name="Warnings">読み飛ばした行などの警告。1件もなくても空リストを返す。</param>
public sealed record ImportResult(
    string Version,
    DateOnly WorkingDayRangeStart,
    DateOnly WorkingDayRangeEnd,
    DateOnly? MilestoneRangeStart,
    DateOnly? MilestoneRangeEnd,
    IReadOnlyList<DateOnly> WorkingDays,
    IReadOnlyList<Milestone> Milestones,
    IReadOnlyList<string> Warnings)
{
    /// <summary>この取り込み結果だけで新しいカレンダーを作る。</summary>
    public WorkingDayCalendar ToCalendar() =>
        WorkingDayCalendar.Create(
            WorkingDays,
            WorkingDayRangeStart, WorkingDayRangeEnd,
            Milestones,
            MilestoneRangeStart, MilestoneRangeEnd);

    /// <summary>マイルストーンに現れた種類名（重複なし・出現順）。色の自動割り当てに使う。</summary>
    public IReadOnlyList<string> MilestoneNames =>
        Milestones.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
}
