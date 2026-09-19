namespace SlideinaCalendar.Data.Models;

/// <summary>
/// カレンダー1つ。
/// <para>
/// これまでは予定の <c>calendar_id</c> から名前を拾い、色は名前から作っていた。
/// Google から取り込めば本物の名前と色になる（要件書 6.3）。
/// </para>
/// </summary>
public sealed record CalendarSource
{
    /// <summary>Google 側のカレンダー ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Google 側の名前。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>利用者が付け替えた表示名。Google Calendar の <c>summaryOverride</c>。</summary>
    public string? SummaryOverride { get; init; }

    /// <summary>画面に出す名前。付け替えがあればそちらを使う。</summary>
    public string DisplayName => SummaryOverride is { Length: > 0 } name ? name : Summary;

    /// <summary>色（<c>#rrggbb</c>）。予定の帯と左パネルの色見本に使う。</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>文字色（<c>#rrggbb</c>）。</summary>
    public string? ForegroundColor { get; init; }

    /// <summary>既定のカレンダーか。</summary>
    public bool IsPrimary { get; init; }

    /// <summary>左パネルでチェックが入っているか。</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>並び順。</summary>
    public int SortOrder { get; init; }

    /// <summary>最後に Google から受け取った姿。</summary>
    public string? GoogleRaw { get; init; }

    /// <summary>ローカルでの更新時刻。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>タスクリスト1つ。カレンダーとは独立した同期経路を持つ（要件書 6.3）。</summary>
public sealed record TaskListSource
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>画面に出す名前。カレンダー側と揃えておく。</summary>
    public string DisplayName => Title;

    public bool IsVisible { get; init; } = true;

    public int SortOrder { get; init; }

    public string? GoogleRaw { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
