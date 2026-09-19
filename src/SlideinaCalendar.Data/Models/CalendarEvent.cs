namespace SlideinaCalendar.Data.Models;

/// <summary>
/// 予定。
/// <para>
/// タスクとは<b>別テーブル・別同期経路</b>で保持する（要件書 3.1）。Google Calendar と
/// Google Tasks が別 API である以上、ここを混ぜると同期のたびに変換が入り不整合の温床になる。
/// 統合は ViewModel 層でのみ行う。
/// </para>
/// <para>
/// 位置引数ではなくプロパティ初期化子で組み立てる形にしてある。項目が多く、
/// 呼び出し側で順番を取り違えやすいのと、Dapper が結果から組み立てるのに
/// 引数なしのコンストラクタを要するため。
/// </para>
/// </summary>
public sealed record CalendarEvent
{
    /// <summary>ローカルの識別子。旧データからの移行では元の uid をそのまま使う。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>タイトル。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>開始日。</summary>
    public DateOnly Date { get; init; }

    /// <summary>複数日にまたがる場合の終了日。単日なら null。</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>開始時刻。null なら終日予定。</summary>
    public TimeOnly? StartTime { get; init; }

    /// <summary>終了時刻。<see cref="StartTime"/> があれば必ず入る。</summary>
    public TimeOnly? EndTime { get; init; }

    /// <summary>場所。</summary>
    public string? Location { get; init; }

    /// <summary>説明。Google Calendar の <c>description</c>。</summary>
    public string? Note { get; init; }

    /// <summary>
    /// 関連する URL。Google Calendar の <c>source.url</c>。
    /// <para>資料や図面の置き場所。説明欄に書くと本文と混ざって拾いにくい。</para>
    /// </summary>
    public string? Url { get; init; }

    /// <summary>色（<c>#rrggbb</c>）。</summary>
    public string? Color { get; init; }

    /// <summary>所属カレンダー。Google の複数カレンダーに対応する。</summary>
    public string? CalendarId { get; init; }

    /// <summary>繰り返し指定（<c>FREQ=...</c>）。単発なら null。</summary>
    public string? Recurrence { get; init; }

    /// <summary>Google Calendar 側のイベント ID。未同期なら null。</summary>
    public string? GoogleEventId { get; init; }

    /// <summary>Google 側の更新時刻。差分判定に使う。</summary>
    public string? GoogleUpdated { get; init; }

    /// <summary>取り込み元。</summary>
    public string? Source { get; init; }

    /// <summary>ローカルでの更新時刻。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>終日予定か。</summary>
    public bool IsAllDay => StartTime is null;

    /// <summary>繰り返し予定か。</summary>
    public bool IsRecurring => !string.IsNullOrEmpty(Recurrence);

    /// <summary>この予定が占める最終日。<see cref="EndDate"/> が無ければ開始日と同じ。</summary>
    public DateOnly LastDate => EndDate ?? Date;
}
