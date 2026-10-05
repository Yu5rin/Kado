using Kado.Core.Recurrence;

namespace Kado.Presentation.Editing;

/// <summary>タスクの繰り返しの選択肢1つ。</summary>
/// <param name="Key">選択肢の識別子（<see cref="TaskRepeatChoice"/> の定数か、<see cref="TaskRepeatKind"/> の名前）。</param>
/// <param name="Label">「毎週 月曜日」のような表示。期限日によって変わる。</param>
public sealed record TaskRepeatOption(string Key, string Label)
{
    /// <summary>表示名をそのまま返す。理由は <see cref="RecurrenceOption.ToString"/> と同じ。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// タスクの編集画面で選べる繰り返しと、指定文字列（<see cref="TaskRepeat"/>）を行き来する。
/// <para>
/// 暦どおりの4つ（毎日・毎週・毎月・毎年）の文言は、予定の繰り返し欄（<see cref="RecurrenceChoice"/>）の
/// ものをそのまま使う。期限日に合わせて具体的に出す（「毎週 月曜日」「毎月 31日」）。
/// 稼働日が基準の4つは、期限日に関わらず同じ文言。
/// </para>
/// </summary>
public static class TaskRepeatChoice
{
    /// <summary>繰り返さない。</summary>
    public const string None = "None";

    /// <summary>
    /// 読めない指定。取り込んだ・手で書き換えたなどで、選択肢で表せない文字列を持っているとき。
    /// 選択肢に無いからと消すと、保存し直すだけで失われる。元の文字列を持ち続ける。
    /// </summary>
    public const string Custom = "Custom";

    /// <summary>読めない指定を持っているときの文言。繰り返しは動かない。</summary>
    public const string CustomLabel = "読めない指定のまま（繰り返しません）";

    /// <summary>画面に小さく出す一行。Google のアプリの繰り返しとは別物であることを伝える。</summary>
    public const string KadoOnlyNote =
        "繰り返しは Kado だけで動きます。Google のアプリで同じタスクに繰り返しを付けると二重になります";

    /// <summary>期限の無いタスクに繰り返しを選べない理由。</summary>
    public const string NeedsDueReason = "繰り返しは、期限を付けると選べます";

    /// <summary>サブタスクに繰り返しを選べない理由。</summary>
    public const string SubtaskReason = "サブタスクには繰り返しを付けられません";

    /// <summary>
    /// 期限日に合わせた選択肢。<paramref name="includeCustom"/> は読めない指定を持っているときだけ true。
    /// </summary>
    public static IReadOnlyList<TaskRepeatOption> OptionsFor(DateOnly due, bool includeCustom)
    {
        // 暦どおりの4つの文言は、予定の繰り返し欄と同じ（毎日・毎週 ○曜日・毎月 ○日・毎年 ○月○日）
        var calendar = RecurrenceChoice.OptionsFor(due, includeCustom: false);

        string LabelOf(RecurrenceKind kind) => calendar.First(o => o.Kind == kind).Label;

        var options = new List<TaskRepeatOption>(10)
        {
            new(None, LabelOf(RecurrenceKind.None)),
            new(nameof(TaskRepeatKind.Daily), LabelOf(RecurrenceKind.Daily)),
            new(nameof(TaskRepeatKind.Weekly), LabelOf(RecurrenceKind.Weekly)),
            new(nameof(TaskRepeatKind.Monthly), LabelOf(RecurrenceKind.Monthly)),
            new(nameof(TaskRepeatKind.Yearly), LabelOf(RecurrenceKind.Yearly)),
            new(nameof(TaskRepeatKind.WeekFirstWorkday), "毎週の週始め（最初の稼働日）"),
            new(nameof(TaskRepeatKind.WeekLastWorkday), "毎週の週終わり（最後の稼働日）"),
            new(nameof(TaskRepeatKind.MonthFirstWorkday), "毎月の月初（最初の稼働日）"),
            new(nameof(TaskRepeatKind.MonthLastWorkday), "毎月の月末（最後の稼働日）"),
        };

        if (includeCustom) options.Add(new TaskRepeatOption(Custom, CustomLabel));

        return options;
    }

    /// <summary>
    /// 指定文字列から選択肢を読む。空なら「繰り返さない」、読めなければ「読めない指定」
    /// （消すと保存し直すだけで失われるので、「繰り返さない」には倒さない）。
    /// </summary>
    public static string KeyOf(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return None;

        return TaskRepeat.Parse(spec) is { } rule ? rule.Kind.ToString() : Custom;
    }

    /// <summary>
    /// 選択肢から指定文字列を作る。暦どおりの4つは <paramref name="due"/> の曜日・日付で明示する。
    /// 「読めない指定」は元の文字列を返す（消さない）。
    /// </summary>
    public static string? ToSpec(string key, DateOnly due, string? original)
    {
        if (key == None) return null;
        if (key == Custom) return original;

        return Enum.TryParse<TaskRepeatKind>(key, out var kind) && Enum.IsDefined(kind)
            ? TaskRepeat.ToSpec(kind, due)
            : null;
    }
}
