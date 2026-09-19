using Dapper;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Data.Repositories;

/// <summary>
/// タスクと作業時間ブロックの読み書き。
/// <para>予定とは独立した経路で扱う（要件書 3.1）。</para>
/// </summary>
public sealed class TaskRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private const string Columns = """
        id AS Id, title AS Title, due AS Due, is_done AS IsDone,
        note AS Note, task_list_id AS TaskListId,
        completed_at AS CompletedAt, parent_id AS ParentId, position AS Position,
        google_raw AS GoogleRaw,
        google_task_id AS GoogleTaskId, google_task_list_id AS GoogleTaskListId,
        google_updated AS GoogleUpdated, source AS Source, updated_at AS UpdatedAt
        """;

    /// <summary>1件取得する。無ければ null。</summary>
    public TaskItem? Find(string id) =>
        _connection.QuerySingleOrDefault<TaskItem>(
            $"SELECT {Columns} FROM tasks WHERE id = @id;", new { id });

    /// <summary>全件。期限なしが後ろに来るよう並べる。</summary>
    public IReadOnlyList<TaskItem> All() =>
        _connection.Query<TaskItem>(
            $"SELECT {Columns} FROM tasks ORDER BY due IS NULL, due, title;").ToArray();

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
            ORDER BY due, title;
            """,
            new { from = SqliteTypeHandlers.ToText(from), to = SqliteTypeHandlers.ToText(to) }).ToArray();

    /// <summary>期限が決まっていないタスク。</summary>
    public IReadOnlyList<TaskItem> WithoutDue() =>
        _connection.Query<TaskItem>(
            $"SELECT {Columns} FROM tasks WHERE due IS NULL ORDER BY title;").ToArray();

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
                id, title, due, is_done, note, task_list_id,
                completed_at, parent_id, position, google_raw,
                google_task_id, google_task_list_id, google_updated, source, updated_at
            ) VALUES (
                @Id, @Title, @Due, @IsDone, @Note, @TaskListId,
                @CompletedAt, @ParentId, @Position, @GoogleRaw,
                @GoogleTaskId, @GoogleTaskListId, @GoogleUpdated, @Source, @UpdatedAt
            )
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, due = excluded.due, is_done = excluded.is_done,
                note = excluded.note, task_list_id = excluded.task_list_id,
                completed_at = excluded.completed_at, parent_id = excluded.parent_id,
                position = excluded.position, google_raw = excluded.google_raw,
                google_task_id = excluded.google_task_id,
                google_task_list_id = excluded.google_task_list_id,
                google_updated = excluded.google_updated,
                source = excluded.source, updated_at = excluded.updated_at;
            """,
            value, transaction);
    }

    /// <summary>まとめて登録する。</summary>
    public void UpsertMany(IEnumerable<TaskItem> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var transaction = _connection.BeginTransaction();
        foreach (var value in values) Upsert(value, transaction);
        transaction.Commit();
    }

    /// <summary>削除する。作業時間ブロックも連鎖して消える。</summary>
    public bool Delete(string id, SqliteTransaction? transaction = null) =>
        _connection.Execute("DELETE FROM tasks WHERE id = @id;", new { id }, transaction) > 0;

    // ------------------------------------------------------------------
    // 作業時間ブロック
    // ------------------------------------------------------------------

    /// <summary>指定タスクの作業時間ブロック。</summary>
    public IReadOnlyList<WorkBlock> BlocksOf(string taskId) =>
        _connection.Query<WorkBlock>(
            """
            SELECT id AS Id, task_id AS TaskId, date AS Date,
                   start_time AS StartTime, duration_minutes AS DurationMinutes
            FROM work_blocks WHERE task_id = @taskId ORDER BY date, start_time;
            """, new { taskId }).ToArray();

    /// <summary>期間内の作業時間ブロック。週・日ビューの時間軸に並べる。</summary>
    public IReadOnlyList<WorkBlock> BlocksInRange(DateOnly from, DateOnly to) =>
        _connection.Query<WorkBlock>(
            """
            SELECT id AS Id, task_id AS TaskId, date AS Date,
                   start_time AS StartTime, duration_minutes AS DurationMinutes
            FROM work_blocks WHERE date BETWEEN @from AND @to ORDER BY date, start_time;
            """,
            new { from = SqliteTypeHandlers.ToText(from), to = SqliteTypeHandlers.ToText(to) }).ToArray();

    /// <summary>作業時間ブロックを登録または更新する。</summary>
    public void UpsertBlock(WorkBlock value, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO work_blocks (id, task_id, date, start_time, duration_minutes)
            VALUES (@Id, @TaskId, @Date, @StartTime, @DurationMinutes)
            ON CONFLICT (id) DO UPDATE SET
                task_id = excluded.task_id, date = excluded.date,
                start_time = excluded.start_time, duration_minutes = excluded.duration_minutes;
            """,
            value, transaction);
    }

    /// <summary>作業時間ブロックを削除する。</summary>
    public bool DeleteBlock(string id, SqliteTransaction? transaction = null) =>
        _connection.Execute("DELETE FROM work_blocks WHERE id = @id;", new { id }, transaction) > 0;
}
