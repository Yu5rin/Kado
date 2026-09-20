using System.Globalization;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>検索で見つかった1件。予定とタスクのどちらもここに収める。</summary>
public sealed class SearchResultViewModel
{
    private SearchResultViewModel(string id, string title, DateOnly date, bool isTask, string? detail)
    {
        Id = id;
        Title = title;
        Date = date;
        IsTask = isTask;
        Detail = detail;
    }

    /// <summary>予定またはタスクの識別子。</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>予定の日、またはタスクの期限。</summary>
    public DateOnly Date { get; }

    /// <summary>タスクか。丸印を添えて予定と見分ける。</summary>
    public bool IsTask { get; }

    /// <summary>場所やメモの一部。何に当たったのかが分かるように添える。</summary>
    public string? Detail { get; }

    /// <summary>「2026/9/24（木）」。</summary>
    public string DateText => Date.ToString("yyyy/M/d（ddd）", JapaneseCulture);

    /// <summary>予定から組み立てる。</summary>
    public static SearchResultViewModel Of(CalendarEvent value) =>
        new(value.Id, value.Title, value.Date, isTask: false,
            value.Location is { Length: > 0 } place ? place : Trim(value.Note));

    /// <summary>タスクから組み立てる。期限の無いタスクは対象にしない。</summary>
    public static SearchResultViewModel Of(TaskItem value, DateOnly due) =>
        new(value.Id, value.Title, due, isTask: true, Trim(value.Note));

    /// <summary>メモは長いので頭だけ。</summary>
    private static string? Trim(string? note) =>
        note is { Length: > 0 }
            ? note.ReplaceLineEndings(" ") is var flat && flat.Length > 40 ? flat[..40] + "…" : flat
            : null;

    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
}
