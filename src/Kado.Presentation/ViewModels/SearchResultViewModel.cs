using System.Globalization;
using Kado.Data.Models;

namespace Kado.Presentation.ViewModels;

/// <summary>検索で見つかった1件。予定とタスクのどちらもここに収める。</summary>
public sealed class SearchResultViewModel
{
    private SearchResultViewModel(string id, string title, DateOnly date, bool isTask, string? detail,
        bool hasDue = true)
    {
        Id = id;
        Title = title;
        Date = date;
        IsTask = isTask;
        Detail = detail;
        HasDue = hasDue;
    }

    /// <summary>予定またはタスクの識別子。</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>
    /// 予定の日、またはタスクの期限。
    /// <para>
    /// 期限の無いタスクは並び替えに日付が要るため、<see cref="Of(TaskItem,DateOnly)"/>
    /// の呼び出し側が選んでいる日で代用する。画面には <see cref="DateText"/> 側で
    /// 「期限なし」と出すので、この代用値がそのまま見えることはない。
    /// </para>
    /// </summary>
    public DateOnly Date { get; }

    /// <summary>タスクか。丸印を添えて予定と見分ける。</summary>
    public bool IsTask { get; }

    /// <summary>期限（または予定の日）を持っているか。</summary>
    public bool HasDue { get; }

    /// <summary>場所やメモの一部。何に当たったのかが分かるように添える。</summary>
    public string? Detail { get; }

    /// <summary>「2026/9/24（木）」。期限の無いタスクは「期限なし」。</summary>
    public string DateText => HasDue ? Date.ToString("yyyy/M/d（ddd）", JapaneseCulture) : "期限なし";

    /// <summary>予定から組み立てる。</summary>
    public static SearchResultViewModel Of(CalendarEvent value) =>
        new(value.Id, value.Title, value.Date, isTask: false,
            value.Location is { Length: > 0 } place ? place : Trim(value.Note));

    /// <summary>
    /// タスクから組み立てる。
    /// <para>
    /// 期限の無いタスクも対象にする（要件書 3.1）。保存はされるのに検索からも
    /// 見えなくなっていた分。並びに日付が要るので、期限が無ければ呼び出し側が
    /// 選択日で代用した値を <paramref name="sortDate"/> に渡す。画面には出さない。
    /// </para>
    /// </summary>
    public static SearchResultViewModel Of(TaskItem value, DateOnly sortDate) =>
        new(value.Id, value.Title, sortDate, isTask: true, Trim(value.Note), hasDue: value.HasDue);

    /// <summary>メモは長いので頭だけ。</summary>
    private static string? Trim(string? note) =>
        note is { Length: > 0 }
            ? note.ReplaceLineEndings(" ") is var flat && flat.Length > 40 ? flat[..40] + "…" : flat
            : null;

    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
}
