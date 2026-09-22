namespace SlideinaCalendar.Data.Models;

/// <summary>
/// タスク。
/// <para>
/// 予定とは別に持つ（要件書 3.1）。「期限だけある」「いつやるか未定」という状態を
/// 持てる必要があり、開始・終了時刻が前提の予定と同じ型には収まらないため。
/// </para>
/// <para>
/// <see cref="WorkBlock"/>（作業時間ブロック）は<b>タスク側の属性</b>であり、予定には変換しない。
/// UI 上は週・日ビューの時間軸に置かれるが、データとしてはタスクのまま扱う。
/// </para>
/// </summary>
public sealed record TaskItem
{
    /// <summary>ローカルの識別子。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>タイトル。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>期限日。決まっていなければ null。</summary>
    public DateOnly? Due { get; init; }

    /// <summary>完了したか。</summary>
    public bool IsDone { get; init; }

    /// <summary>メモ。</summary>
    public string? Note { get; init; }

    /// <summary>所属タスクリスト。</summary>
    public string? TaskListId { get; init; }

    /// <summary>
    /// 完了した日時。未完了なら null。
    /// <para>完了したことだけでは、ローカルと Google のどちらが新しいか判定できない。</para>
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>親タスク。サブタスクでなければ null。Google 側では <c>move</c> でしか変えられない。</summary>
    public string? ParentId { get; init; }

    /// <summary>同じ階層での並び順。Google が振る文字列をそのまま持つ。</summary>
    public string? Position { get; init; }

    /// <summary>最後に Google から受け取った姿。用途は予定側と同じ。</summary>
    public string? GoogleRaw { get; init; }

    /// <summary>Google Tasks 側の ID。未同期なら null。</summary>
    public string? GoogleTaskId { get; init; }

    /// <summary>Google Tasks 側のリスト ID。</summary>
    public string? GoogleTaskListId { get; init; }

    /// <summary>Google 側の更新時刻。</summary>
    public string? GoogleUpdated { get; init; }

    /// <summary>取り込み元。</summary>
    public string? Source { get; init; }

    /// <summary>ローカルでの更新時刻。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// 作成日時。並びの既定（期限日順・登録が古い順）の基準になる。
    /// <para>
    /// 既存のデータベース（V6 より前）には無かったので、更新日時で埋めてある
    /// （<see cref="Migrations.SchemaMigrations"/> の V6 を参照）。本当の作成日時
    /// ではないが、無いよりはましという位置づけ。
    /// </para>
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// 同じ期限日のタスクどうしの並び順。手で並べ替えたときだけ動く（既定は 0）。
    /// <para>
    /// 期限日をまたいだ比較はしない。並びはまず期限日（<see cref="Due"/>）が決め、
    /// 同じ期限日の中でだけこの値が効く。
    /// </para>
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>期限が決まっているか。</summary>
    public bool HasDue => Due is not null;
}

/// <summary>
/// 作業時間ブロック。タスクを時間軸へドラッグしたときに確保される作業時間。
/// <para>予定ではないので、表示も点線枠で区別する（要件書 5.4）。</para>
/// </summary>
public sealed record WorkBlock
{
    /// <summary>識別子。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>どのタスクの作業時間か。</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>日付。</summary>
    public DateOnly Date { get; init; }

    /// <summary>開始時刻。</summary>
    public TimeOnly StartTime { get; init; }

    /// <summary>所要時間（分）。</summary>
    public int DurationMinutes { get; init; }

    /// <summary>終了時刻。</summary>
    public TimeOnly EndTime => StartTime.AddMinutes(DurationMinutes);
}
