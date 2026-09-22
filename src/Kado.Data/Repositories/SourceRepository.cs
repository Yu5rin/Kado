using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Models;

namespace Kado.Data.Repositories;

/// <summary>
/// カレンダーとタスクリストの読み書き。
/// <para>
/// 同期を始めるまでは空のことがある。そのときは呼び出し側が予定とタスクの
/// 所属 ID から組み立てる。取り込んだら本物の名前と色に差し替わる。
/// </para>
/// </summary>
public sealed class SourceRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private const string CalendarColumns = """
        id AS Id, summary AS Summary, summary_override AS SummaryOverride,
        background_color AS BackgroundColor, foreground_color AS ForegroundColor,
        is_primary AS IsPrimary, is_visible AS IsVisible, sort_order AS SortOrder,
        notify_default AS NotifyDefault,
        google_raw AS GoogleRaw, updated_at AS UpdatedAt
        """;

    private const string TaskListColumns = """
        id AS Id, title AS Title, is_visible AS IsVisible, sort_order AS SortOrder,
        google_raw AS GoogleRaw, updated_at AS UpdatedAt
        """;

    /// <summary>1件取る。無ければ null。</summary>
    public CalendarSource? FindCalendar(string id) =>
        _connection.QuerySingleOrDefault<CalendarSource>(
            $"SELECT {CalendarColumns} FROM calendars WHERE id = @id;", new { id });

    /// <summary>カレンダーを並び順で取る。</summary>
    public IReadOnlyList<CalendarSource> Calendars() =>
        _connection.Query<CalendarSource>(
            $"SELECT {CalendarColumns} FROM calendars ORDER BY sort_order, summary;").ToArray();

    /// <summary>タスクリストを1件取る。無ければ null。</summary>
    public TaskListSource? FindTaskList(string id) =>
        _connection.QuerySingleOrDefault<TaskListSource>(
            $"SELECT {TaskListColumns} FROM task_lists WHERE id = @id;", new { id });

    /// <summary>タスクリストを並び順で取る。</summary>
    public IReadOnlyList<TaskListSource> TaskLists() =>
        _connection.Query<TaskListSource>(
            $"SELECT {TaskListColumns} FROM task_lists ORDER BY sort_order, title;").ToArray();

    /// <summary>カレンダーを登録または更新する。</summary>
    public void Upsert(CalendarSource value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO calendars (
                id, summary, summary_override, background_color, foreground_color,
                is_primary, is_visible, sort_order, notify_default, google_raw, updated_at
            ) VALUES (
                @Id, @Summary, @SummaryOverride, @BackgroundColor, @ForegroundColor,
                @IsPrimary, @IsVisible, @SortOrder, @NotifyDefault, @GoogleRaw, @UpdatedAt
            )
            ON CONFLICT (id) DO UPDATE SET
                summary = excluded.summary, summary_override = excluded.summary_override,
                background_color = excluded.background_color,
                foreground_color = excluded.foreground_color,
                is_primary = excluded.is_primary, sort_order = excluded.sort_order,
                google_raw = excluded.google_raw, updated_at = excluded.updated_at;
            """,
            value);
    }

    /// <summary>タスクリストを登録または更新する。</summary>
    public void Upsert(TaskListSource value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO task_lists (id, title, is_visible, sort_order, google_raw, updated_at)
            VALUES (@Id, @Title, @IsVisible, @SortOrder, @GoogleRaw, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, sort_order = excluded.sort_order,
                google_raw = excluded.google_raw, updated_at = excluded.updated_at;
            """,
            value);
    }

    /// <summary>
    /// 名前と色を書き換える。
    /// <para>
    /// Google から取り込んだカレンダーでも、こちら側の表示名として
    /// <c>summary_override</c> に入れる。元の名前は上書きしない。次の同期で戻ってしまう。
    /// </para>
    /// </summary>
    /// <returns>対象が見つかって書き換えたら true。</returns>
    public bool UpdateCalendar(string id, string name, string? backgroundColor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _connection.Execute(
            """
            UPDATE calendars
            SET summary_override = @name, background_color = @backgroundColor, updated_at = @updatedAt
            WHERE id = @id;
            """,
            new { id, name = name.Trim(), backgroundColor, updatedAt = DateTimeOffset.Now }) > 0;
    }

    /// <summary>タスクリストの名前を書き換える。</summary>
    public bool UpdateTaskList(string id, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return _connection.Execute(
            "UPDATE task_lists SET title = @title, updated_at = @updatedAt WHERE id = @id;",
            new { id, title = title.Trim(), updatedAt = DateTimeOffset.Now }) > 0;
    }

    /// <summary>
    /// カレンダーを消し、そこに入っていた予定を別のカレンダーへ移す。
    /// <para>
    /// 予定ごと消さない。分類を消したかっただけなのに中身まで消えるのは行き過ぎで、
    /// しかも Undo に積めない（まとめて書き換えるため）。
    /// </para>
    /// </summary>
    /// <returns>移した予定の件数。</returns>
    public int DeleteCalendar(string id, string? moveEventsTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var transaction = _connection.BeginTransaction();

        var moved = _connection.Execute(
            "UPDATE events SET calendar_id = @moveEventsTo WHERE calendar_id = @id;",
            new { id, moveEventsTo }, transaction);

        _connection.Execute("DELETE FROM calendars WHERE id = @id;", new { id }, transaction);
        transaction.Commit();

        return moved;
    }

    /// <summary>タスクリストを消し、そこに入っていたタスクを別のリストへ移す。</summary>
    /// <returns>移したタスクの件数。</returns>
    public int DeleteTaskList(string id, string? moveTasksTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var transaction = _connection.BeginTransaction();

        var moved = _connection.Execute(
            "UPDATE tasks SET task_list_id = @moveTasksTo WHERE task_list_id = @id;",
            new { id, moveTasksTo }, transaction);

        _connection.Execute("DELETE FROM task_lists WHERE id = @id;", new { id }, transaction);
        transaction.Commit();

        return moved;
    }

    /// <summary>そのカレンダーに入っている予定の件数。消す前に知らせるために使う。</summary>
    public int EventCountIn(string calendarId) =>
        _connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM events WHERE calendar_id = @calendarId;", new { calendarId });

    /// <summary>そのタスクリストに入っているタスクの件数。</summary>
    public int TaskCountIn(string taskListId) =>
        _connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM tasks WHERE task_list_id = @taskListId;", new { taskListId });

    /// <summary>次に足すときの並び順。末尾に置く。</summary>
    public int NextCalendarOrder() =>
        _connection.ExecuteScalar<int?>("SELECT MAX(sort_order) FROM calendars;") + 1 ?? 0;

    public int NextTaskListOrder() =>
        _connection.ExecuteScalar<int?>("SELECT MAX(sort_order) FROM task_lists;") + 1 ?? 0;

    /// <summary>
    /// 表示のチェックを切り替える。
    /// <para>
    /// 取り込みの Upsert では上書きしない。同期のたびにチェックが戻ると使い物にならない。
    /// </para>
    /// </summary>
    public bool SetCalendarVisible(string id, bool isVisible) =>
        _connection.Execute(
            "UPDATE calendars SET is_visible = @isVisible WHERE id = @id;",
            new { id, isVisible }) > 0;

    /// <summary>
    /// このカレンダーの予定を既定で知らせるかどうかを切り替える。
    /// <para>表示のチェックと同じく、取り込みの Upsert では上書きしない。</para>
    /// </summary>
    public bool SetCalendarNotify(string id, bool notify) =>
        _connection.Execute(
            "UPDATE calendars SET notify_default = @notify WHERE id = @id;",
            new { id, notify }) > 0;

    public bool SetTaskListVisible(string id, bool isVisible) =>
        _connection.Execute(
            "UPDATE task_lists SET is_visible = @isVisible WHERE id = @id;",
            new { id, isVisible }) > 0;

    /// <summary>並び順を入れ替える。</summary>
    public void SetCalendarOrder(IReadOnlyList<string> idsInOrder)
    {
        ArgumentNullException.ThrowIfNull(idsInOrder);

        using var transaction = _connection.BeginTransaction();

        for (var i = 0; i < idsInOrder.Count; i++)
        {
            _connection.Execute(
                "UPDATE calendars SET sort_order = @order WHERE id = @id;",
                new { id = idsInOrder[i], order = i }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>タスクリストの並び順を入れ替える。</summary>
    public void SetTaskListOrder(IReadOnlyList<string> idsInOrder)
    {
        ArgumentNullException.ThrowIfNull(idsInOrder);

        using var transaction = _connection.BeginTransaction();

        for (var i = 0; i < idsInOrder.Count; i++)
        {
            _connection.Execute(
                "UPDATE task_lists SET sort_order = @order WHERE id = @id;",
                new { id = idsInOrder[i], order = i }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>Google から消えたカレンダーを落とす。予定そのものは消さない。</summary>
    public int RemoveCalendarsExcept(IReadOnlyList<string> keepIds)
    {
        ArgumentNullException.ThrowIfNull(keepIds);

        return keepIds.Count == 0
            ? _connection.Execute("DELETE FROM calendars;")
            : _connection.Execute(
                "DELETE FROM calendars WHERE id NOT IN @keepIds;", new { keepIds });
    }

    public int RemoveTaskListsExcept(IReadOnlyList<string> keepIds)
    {
        ArgumentNullException.ThrowIfNull(keepIds);

        return keepIds.Count == 0
            ? _connection.Execute("DELETE FROM task_lists;")
            : _connection.Execute(
                "DELETE FROM task_lists WHERE id NOT IN @keepIds;", new { keepIds });
    }
}
