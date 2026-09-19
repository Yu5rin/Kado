using Dapper;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Data.Repositories;

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
        google_raw AS GoogleRaw, updated_at AS UpdatedAt
        """;

    private const string TaskListColumns = """
        id AS Id, title AS Title, is_visible AS IsVisible, sort_order AS SortOrder,
        google_raw AS GoogleRaw, updated_at AS UpdatedAt
        """;

    /// <summary>カレンダーを並び順で取る。</summary>
    public IReadOnlyList<CalendarSource> Calendars() =>
        _connection.Query<CalendarSource>(
            $"SELECT {CalendarColumns} FROM calendars ORDER BY sort_order, summary;").ToArray();

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
                is_primary, is_visible, sort_order, google_raw, updated_at
            ) VALUES (
                @Id, @Summary, @SummaryOverride, @BackgroundColor, @ForegroundColor,
                @IsPrimary, @IsVisible, @SortOrder, @GoogleRaw, @UpdatedAt
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
    /// 表示のチェックを切り替える。
    /// <para>
    /// 取り込みの Upsert では上書きしない。同期のたびにチェックが戻ると使い物にならない。
    /// </para>
    /// </summary>
    public bool SetCalendarVisible(string id, bool isVisible) =>
        _connection.Execute(
            "UPDATE calendars SET is_visible = @isVisible WHERE id = @id;",
            new { id, isVisible }) > 0;

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
