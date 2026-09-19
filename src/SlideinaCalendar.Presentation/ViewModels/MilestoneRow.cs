using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>
/// 日付の行に並べるマイルストーン。
/// <para>
/// 月・週・日の3か所で同じものを出すので、組み立てはここに集める。以前は3か所それぞれが
/// 実働日データから直に引いていて、左パネルのチェックが効かなかった。
/// </para>
/// <para>
/// 材料は<b>予定</b>。実働日 Excel を取り込むと、マイルストーンは「inaCalendar」の予定
/// としても書き出される。予定から作れば、所属カレンダーのチェックがそのまま効く。
/// </para>
/// </summary>
public static class MilestoneRow
{
    /// <summary>その日の分を組み立てる。</summary>
    /// <param name="date">この日。</param>
    /// <param name="events">その日に重なる予定。</param>
    /// <param name="filter">出すかどうかの判断。渡さなければ実働日データ由来のものだけ。</param>
    public static IReadOnlyList<Milestone> For(
        DateOnly date, IEnumerable<ScheduledEvent>? events, ISourceFilter? filter)
    {
        if (events is null) return [];

        var decide = filter ?? DefaultCalendarSources.Instance;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Milestone>();

        foreach (var scheduled in events)
        {
            if (!decide.IncludesMilestone(scheduled.Source)) continue;

            // 実働日 Excel と Google の inaCalendar に同じものが入っていることがある。
            // 旧 inaCalendar が Google 側にも書き込んでいたため。同じ名前は1つにする
            if (!seen.Add(scheduled.Source.Title)) continue;

            result.Add(new Milestone(date, scheduled.Source.Title));
        }

        return result;
    }
}
