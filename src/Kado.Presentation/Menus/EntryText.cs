using System.Globalization;
using Kado.Data.Models;

namespace Kado.Presentation.Menus;

/// <summary>
/// 右クリックメニューの「題名と日時をコピー」「題名をコピー」で、クリップボードに置く1行。
/// <para>
/// 日時の書き方は決まっていて、人に送る文面にそのまま貼れる形にする。年は書かない（今年の予定が
/// ほとんどで、書くと長い）。時間に頼らない（今日との比較で形が変わらない）ので、同じ予定からは
/// いつでも同じ文字になる。
/// </para>
/// <list type="bullet">
/// <item>時刻付き：<c>10/6(月) 9:00–10:00 打ち合わせ</c></item>
/// <item>終日：<c>10/6(月) 終日 棚卸し</c></item>
/// <item>複数日：<c>10/6(月)–10/8(水) 出張</c>（時刻付きなら <c>10/6(月) 9:00–10/8(水) 17:00 出張</c>）</item>
/// <item>場所があれば後ろに <c>（第2会議室）</c></item>
/// </list>
/// </summary>
public static class EntryText
{
    private const string Weekdays = "日月火水木金土";

    /// <summary>予定の1行。</summary>
    /// <param name="value">予定。</param>
    /// <param name="occurrence">繰り返しの予定のとき、右クリックされた回の日付。繰り返さない予定では使わない。</param>
    public static string ForEvent(CalendarEvent value, DateOnly? occurrence = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        var first = value.IsRecurring && occurrence is { } day ? day : value.Date;
        var length = value.EndDate is { } end ? end.DayNumber - value.Date.DayNumber : 0;
        var last = first.AddDays(Math.Max(length, 0));
        var multiDay = last > first;

        string when;

        if (value.StartTime is { } start)
        {
            var from = Clock(start);
            var to = value.EndTime is { } finish ? Clock(finish) : null;

            when = multiDay
                ? to is null ? $"{Day(first)} {from}–{Day(last)}" : $"{Day(first)} {from}–{Day(last)} {to}"
                : to is null ? $"{Day(first)} {from}" : $"{Day(first)} {from}–{to}";
        }
        else
        {
            when = multiDay ? $"{Day(first)}–{Day(last)}" : $"{Day(first)} 終日";
        }

        var text = $"{when} {value.Title}";

        return value.Location is { Length: > 0 } location ? $"{text}（{location}）" : text;
    }

    /// <summary>タスクの1行。期限があれば <c>10/6(月) 期限 題名</c>、無ければ題名だけ。</summary>
    public static string ForTask(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Due is { } due ? $"{Day(due)} 期限 {value.Title}" : value.Title;
    }

    /// <summary>「10/6(月)」。</summary>
    public static string Day(DateOnly date) => $"{date.Month}/{date.Day}({Weekdays[(int)date.DayOfWeek]})";

    /// <summary>「9:00」。先頭の0は付けない。</summary>
    private static string Clock(TimeOnly time) => time.ToString("H:mm", CultureInfo.InvariantCulture);
}
