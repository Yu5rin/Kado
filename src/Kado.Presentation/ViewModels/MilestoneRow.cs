using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 日付の行に並べるラベル1つ。
/// <para>
/// 元の予定の識別子を持つ。ラベルからその予定を開いたり消したりするのに要る。
/// 以前は名前と日付しか持たない値だったので、押しても何もできなかった。
/// </para>
/// </summary>
public sealed class MilestoneViewModel(string id, string name, CalendarEvent? source = null)
{
    /// <summary>元の予定の識別子。</summary>
    public string Id { get; } = id;

    /// <summary>
    /// 元の予定。右クリックメニューの「リンクを開く」「添付を開く」が、開ける先を読むのに使う
    /// （<see cref="Links.OpenTargets.From"/>）。
    /// </summary>
    public CalendarEvent? Source { get; } = source;

    /// <summary>ラベルに出す名前。色もこれで決まる。</summary>
    public string Name { get; } = name;
}

/// <summary>
/// 日付の行に並べるマイルストーン。
/// <para>
/// 月・週・日の3か所で同じものを出すので、組み立てはここに集める。以前は3か所それぞれが
/// 実働日データから直に引いていて、左パネルのチェックが効かなかった。
/// </para>
/// <para>
/// 材料は<b>予定</b>。実働日 Excel を取り込むと、マイルストーンは「Kado」の予定
/// としても書き出される。予定から作れば、所属カレンダーのチェックがそのまま効く。
/// </para>
/// </summary>
public static class MilestoneRow
{
    /// <summary>その日の分を組み立てる。</summary>
    /// <param name="date">この日。使わないが、呼ぶ側の読みやすさのために受ける。</param>
    /// <param name="events">その日に重なる予定。</param>
    /// <param name="filter">出すかどうかの判断。渡さなければ実働日データ由来のものだけ。</param>
    public static IReadOnlyList<MilestoneViewModel> For(
        DateOnly date, IEnumerable<ScheduledEvent>? events, ISourceFilter? filter)
    {
        if (events is null) return [];

        var decide = filter ?? DefaultCalendarSources.Instance;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MilestoneViewModel>();

        foreach (var scheduled in events)
        {
            if (!decide.IncludesMilestone(scheduled.Source)) continue;

            // 「休業日」「特別出勤」は文字として出さない。マスの色で示す。
            // Google カレンダー側では今までどおり文字で見える
            if (IsDayMark(scheduled.Source.Title)) continue;

            // 実働日 Excel と Google の Kado に同じものが入っていることがある。
            // 旧 inaCalendar が Google 側にも書き込んでいたため。同じ名前は1つにする
            if (!seen.Add(scheduled.Source.Title)) continue;

            result.Add(new MilestoneViewModel(scheduled.Source.Id, scheduled.Source.Title, scheduled.Source));
        }

        return result;
    }

    /// <summary>稼働・非稼働を示すだけの印か。実働日データから起こした2つ。</summary>
    private static bool IsDayMark(string title) =>
        string.Equals(title, CalendarWorkspace.ClosedDayTitle, StringComparison.Ordinal) ||
        string.Equals(title, CalendarWorkspace.OpenDayTitle, StringComparison.Ordinal);
}
