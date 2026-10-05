using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Models;

namespace Kado.Data.Repositories;

/// <summary>
/// タスクの読み書き。
/// <para>予定とは独立した経路で扱う（要件書 3.1）。</para>
/// </summary>
public sealed class TaskRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private const string Columns = """
        id AS Id, title AS Title, due AS Due, is_done AS IsDone,
        note AS Note, url AS Url, attachments AS Attachments, repeat AS Repeat, task_list_id AS TaskListId,
        completed_at AS CompletedAt, parent_id AS ParentId, position AS Position,
        google_raw AS GoogleRaw,
        google_task_id AS GoogleTaskId, google_task_list_id AS GoogleTaskListId,
        google_updated AS GoogleUpdated, google_missing AS GoogleMissing,
        source AS Source, updated_at AS UpdatedAt,
        created_at AS CreatedAt, sort_order AS SortOrder
        """;

    /// <summary>1件取得する。無ければ null。</summary>
    public TaskItem? Find(string id) =>
        _connection.QuerySingleOrDefault<TaskItem>(
            $"SELECT {Columns} FROM tasks WHERE id = @id;", new { id });

    /// <summary>
    /// 全件。期限なしが後ろに来るよう並べる。
    /// <para>期限日→並び順→作成日時→識別子の順（要件の既定「期限日順・登録が古い順」）。</para>
    /// </summary>
    public IReadOnlyList<TaskItem> All() =>
        _connection.Query<TaskItem>(
            $"SELECT {Columns} FROM tasks ORDER BY due IS NULL, due, sort_order, created_at, id;").ToArray();

    /// <summary>
    /// 使われているタスクリスト ID を重複なく返す。
    /// <para>リスト自体の表は持たない。理由は <see cref="EventRepository.CalendarIds"/> と同じ。</para>
    /// </summary>
    public IReadOnlyList<string> TaskListIds() =>
        _connection.Query<string>(
            """
            SELECT DISTINCT task_list_id FROM tasks
            WHERE task_list_id IS NOT NULL AND task_list_id <> ''
            ORDER BY task_list_id;
            """).ToArray();

    /// <summary>
    /// 所属リストを持たないタスクを、指定のリストへ入れる。
    /// <para>理由は <see cref="EventRepository.AdoptOrphans"/> と同じ。</para>
    /// </summary>
    /// <returns>入れ直した件数。</returns>
    public int AdoptOrphans(string taskListId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskListId);

        return _connection.Execute(
            """
            UPDATE tasks SET task_list_id = @taskListId
            WHERE task_list_id IS NULL OR task_list_id = '';
            """,
            new { taskListId });
    }

    /// <summary>期限が期間内にあるタスク。期限なしは含まない。</summary>
    public IReadOnlyList<TaskItem> DueInRange(DateOnly from, DateOnly to) =>
        _connection.Query<TaskItem>(
            $"""
            SELECT {Columns} FROM tasks
            WHERE due IS NOT NULL AND due BETWEEN @from AND @to
            ORDER BY due, sort_order, created_at, id;
            """,
            new { from = SqliteTypeHandlers.ToText(from), to = SqliteTypeHandlers.ToText(to) }).ToArray();

    /// <summary>
    /// 期間に現れうるタスク。期限が期間内にあるもの、または完了日時が期間の近くにあるもの。
    /// <para>
    /// 完了したタスクは「完了した日」のマスに出す（<see cref="ScheduleQuery.TasksByDate"/>）。
    /// 完了日時は epoch ミリ秒で持っていて、その日がどこの日付になるかは端末のタイムゾーンで
    /// 決まる。SQL でタイムゾーンを解くことはしないので、ここは<b>前後 1 日ずつ広めに</b>
    /// 引いて取りこぼしを防ぎ、ローカル日付での絞り込みは呼び出し側（<see cref="ScheduleQuery"/>）が行う。
    /// 返すのは「候補」であって、期間に入るかどうかの最終判断ではない。
    /// </para>
    /// <para>期限の索引（<c>ix_tasks_due</c>）と完了日時の索引（<c>ix_tasks_completed</c>）の OR で引く。</para>
    /// </summary>
    public IReadOnlyList<TaskItem> InRange(DateOnly from, DateOnly to)
    {
        // UTC の 0 時から前後 1 日。日付変更線の両端（UTC-12〜+14）まで含めても収まる
        var lower = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var upper = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .ToUnixTimeMilliseconds();

        return _connection.Query<TaskItem>(
            $"""
            SELECT {Columns} FROM tasks
            WHERE (due IS NOT NULL AND due BETWEEN @from AND @to)
               OR (completed_at IS NOT NULL AND completed_at BETWEEN @lower AND @upper)
            ORDER BY due IS NULL, due, sort_order, created_at, id;
            """,
            new
            {
                from = SqliteTypeHandlers.ToText(from),
                to = SqliteTypeHandlers.ToText(to),
                lower,
                upper,
            }).ToArray();
    }

    /// <summary><see cref="SearchText"/> の絞り込み。エスケープの文字は <see cref="TextSearch.LikeEscape"/>。</summary>
    private const string SearchWhere =
        "WHERE title LIKE @pattern ESCAPE '\\' OR note LIKE @pattern ESCAPE '\\'";

    /// <summary>期限が決まっていないタスク。並び順→作成日時→識別子の順（登録順）。</summary>
    public IReadOnlyList<TaskItem> WithoutDue() =>
        _connection.Query<TaskItem>(
            $"SELECT {Columns} FROM tasks WHERE due IS NULL ORDER BY sort_order, created_at, id;").ToArray();

    /// <summary>
    /// 題・メモのどちらかに <paramref name="text"/> を含むタスク。検索（<c>MainViewModel.RunSearch</c>）が使う。
    /// <para>
    /// <see cref="EventRepository.SearchText"/> と同じ作り。返すタスクは <c>Id・Title・Due・Note</c> しか
    /// 入っていない（ほかは既定値）。並びは <see cref="All"/> と同じ。
    /// </para>
    /// </summary>
    public IReadOnlyList<TaskItem> SearchText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0) return [];

        var like = TextSearch.TryCreateLikePattern(text, out var pattern);

        var found = _connection.Query<TaskItem>(
            $"""
            SELECT id AS Id, title AS Title, due AS Due, note AS Note
            FROM tasks
            {(like ? SearchWhere : "")}
            ORDER BY due IS NULL, due, sort_order, created_at, id;
            """,
            new { pattern });

        return found
            .Where(t => TextSearch.Contains(t.Title, text) || TextSearch.Contains(t.Note, text))
            .ToArray();
    }

    /// <summary>Google Tasks 側の ID で引く。</summary>
    public TaskItem? FindByGoogleId(string googleTaskId) =>
        _connection.QuerySingleOrDefault<TaskItem>(
            $"SELECT {Columns} FROM tasks WHERE google_task_id = @googleTaskId;", new { googleTaskId });

    /// <summary>件数。</summary>
    public int Count() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM tasks;");

    /// <summary>未完了の件数。</summary>
    public int CountOpen() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM tasks WHERE is_done = 0;");

    /// <summary>登録または更新する。</summary>
    public void Upsert(TaskItem value, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO tasks (
                id, title, due, is_done, note, url, attachments, repeat, task_list_id,
                completed_at, parent_id, position, google_raw,
                google_task_id, google_task_list_id, google_updated, google_missing, source, updated_at,
                created_at, sort_order
            ) VALUES (
                @Id, @Title, @Due, @IsDone, @Note, @Url, @Attachments, @Repeat, @TaskListId,
                @CompletedAt, @ParentId, @Position, @GoogleRaw,
                @GoogleTaskId, @GoogleTaskListId, @GoogleUpdated, @GoogleMissing, @Source, @UpdatedAt,
                @CreatedAt, @SortOrder
            )
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, due = excluded.due, is_done = excluded.is_done,
                note = excluded.note, url = excluded.url, attachments = excluded.attachments,
                repeat = excluded.repeat,
                task_list_id = excluded.task_list_id,
                completed_at = excluded.completed_at, parent_id = excluded.parent_id,
                position = excluded.position, google_raw = excluded.google_raw,
                google_task_id = excluded.google_task_id,
                google_task_list_id = excluded.google_task_list_id,
                google_updated = excluded.google_updated,
                google_missing = excluded.google_missing,
                source = excluded.source, updated_at = excluded.updated_at,
                created_at = excluded.created_at, sort_order = excluded.sort_order;
            """,
            value, transaction);
    }

    /// <summary>
    /// 新しく足すタスクの並び順。同じ期限日（期限なしなら期限なしどうし）の末尾に置く。
    /// <para>あとから足したタスクが下に付くようにするため（要件どおり）。</para>
    /// </summary>
    public int NextSortOrder(DateOnly? due)
    {
        var max = due is { } d
            ? _connection.ExecuteScalar<int?>(
                "SELECT MAX(sort_order) FROM tasks WHERE due = @due;",
                new { due = SqliteTypeHandlers.ToText(d) })
            : _connection.ExecuteScalar<int?>("SELECT MAX(sort_order) FROM tasks WHERE due IS NULL;");

        return (max ?? -1) + 1;
    }

    /// <summary>
    /// 渡した順に並び順を 0 から振り直す。
    /// <para>
    /// 手での並べ替え（ドラッグ・右クリックの「上へ／下へ移動」）専用。呼び出し側が
    /// 同じ期限日（期限なしなら期限なしどうし）のタスクだけを渡す前提で、ここでは
    /// 期限日を見ない。<see cref="SourceRepository.SetCalendarOrder"/> と同じ作り。
    /// </para>
    /// <para>Undo には積まない（左パネルのカレンダー・タスクリストの並べ替えと同じ扱い）。</para>
    /// </summary>
    public void SetOrder(IReadOnlyList<string> idsInOrder)
    {
        ArgumentNullException.ThrowIfNull(idsInOrder);

        using var transaction = _connection.BeginTransaction();

        for (var i = 0; i < idsInOrder.Count; i++)
        {
            _connection.Execute(
                "UPDATE tasks SET sort_order = @order WHERE id = @id;",
                new { id = idsInOrder[i], order = i }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>まとめて登録する。</summary>
    public void UpsertMany(IEnumerable<TaskItem> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var transaction = _connection.BeginTransaction();
        foreach (var value in values) Upsert(value, transaction);
        transaction.Commit();
    }

    /// <summary>削除する。</summary>
    public bool Delete(string id, SqliteTransaction? transaction = null) =>
        _connection.Execute("DELETE FROM tasks WHERE id = @id;", new { id }, transaction) > 0;
}
