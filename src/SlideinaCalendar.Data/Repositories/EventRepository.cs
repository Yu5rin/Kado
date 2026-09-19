using Dapper;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Data.Repositories;

/// <summary>
/// 予定の読み書き。
/// <para>
/// 繰り返し予定は日付で絞り込めない（保持しているのは開始日だけで、展開後の日付は持たない）。
/// 期間で引くときは、単発の予定を日付で絞ったうえで、繰り返し予定は全件を返して
/// 呼び出し側が <c>RecurrenceRule</c> で展開する。繰り返しは数が少ないため全件でも問題にならない。
/// </para>
/// </summary>
public sealed class EventRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private const string Columns = """
        id AS Id, title AS Title, date AS Date, end_date AS EndDate,
        start_time AS StartTime, end_time AS EndTime,
        location AS Location, note AS Note, color AS Color,
        calendar_id AS CalendarId, recurrence AS Recurrence, url AS Url,
        google_event_id AS GoogleEventId, google_updated AS GoogleUpdated,
        source AS Source, updated_at AS UpdatedAt
        """;

    /// <summary>1件取得する。無ければ null。</summary>
    public CalendarEvent? Find(string id) =>
        _connection.QuerySingleOrDefault<CalendarEvent>(
            $"SELECT {Columns} FROM events WHERE id = @id;", new { id });

    /// <summary>全件。件数が多くなるので、通常は期間で絞ること。</summary>
    public IReadOnlyList<CalendarEvent> All() =>
        _connection.Query<CalendarEvent>($"SELECT {Columns} FROM events ORDER BY date, start_time;").ToArray();

    /// <summary>
    /// 使われているカレンダー ID を重複なく返す。
    /// <para>
    /// カレンダーそのものの表を持たず、予定が持つ所属 ID から引く。Google 側の
    /// カレンダー一覧はまだ取り込んでおらず、ローカルに独立した表を作ると
    /// 同期を始めたときに二重管理になるため。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> CalendarIds() =>
        _connection.Query<string>(
            """
            SELECT DISTINCT calendar_id FROM events
            WHERE calendar_id IS NOT NULL AND calendar_id <> ''
            ORDER BY calendar_id;
            """).ToArray();

    /// <summary>
    /// 期間に重なる<b>単発の</b>予定。複数日予定は終了日まで見て判定する。
    /// 繰り返し予定は <see cref="AllRecurring"/> で取ること。
    /// </summary>
    public IReadOnlyList<CalendarEvent> InRange(DateOnly from, DateOnly to) =>
        _connection.Query<CalendarEvent>(
            $"""
            SELECT {Columns} FROM events
            WHERE recurrence IS NULL
              AND date <= @to
              AND COALESCE(end_date, date) >= @from
            ORDER BY date, start_time;
            """,
            new { from = SqliteTypeHandlers.ToText(from), to = SqliteTypeHandlers.ToText(to) }).ToArray();

    /// <summary>繰り返し予定の全件。呼び出し側で展開する。</summary>
    public IReadOnlyList<CalendarEvent> AllRecurring() =>
        _connection.Query<CalendarEvent>(
            $"SELECT {Columns} FROM events WHERE recurrence IS NOT NULL ORDER BY date;").ToArray();

    /// <summary>Google 側の ID で引く。同期で突き合わせるときに使う。</summary>
    public CalendarEvent? FindByGoogleId(string googleEventId) =>
        _connection.QuerySingleOrDefault<CalendarEvent>(
            $"SELECT {Columns} FROM events WHERE google_event_id = @googleEventId;", new { googleEventId });

    /// <summary>件数。</summary>
    public int Count() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM events;");

    /// <summary>登録または更新する。</summary>
    public void Upsert(CalendarEvent value, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO events (
                id, title, date, end_date, start_time, end_time,
                location, note, color, calendar_id, recurrence, url,
                google_event_id, google_updated, source, updated_at
            ) VALUES (
                @Id, @Title, @Date, @EndDate, @StartTime, @EndTime,
                @Location, @Note, @Color, @CalendarId, @Recurrence, @Url,
                @GoogleEventId, @GoogleUpdated, @Source, @UpdatedAt
            )
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, date = excluded.date, end_date = excluded.end_date,
                start_time = excluded.start_time, end_time = excluded.end_time,
                location = excluded.location, note = excluded.note, color = excluded.color,
                calendar_id = excluded.calendar_id, recurrence = excluded.recurrence,
                url = excluded.url,
                google_event_id = excluded.google_event_id, google_updated = excluded.google_updated,
                source = excluded.source, updated_at = excluded.updated_at;
            """,
            value, transaction);
    }

    /// <summary>まとめて登録する。1件ずつ Upsert するより速い。</summary>
    public void UpsertMany(IEnumerable<CalendarEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var transaction = _connection.BeginTransaction();
        foreach (var value in values) Upsert(value, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// 削除する。tombstone を残すかは呼び出し側の判断
    /// （ローカルだけの整理なのか、Google へも伝播させるのかで変わる）。
    /// </summary>
    public bool Delete(string id, SqliteTransaction? transaction = null) =>
        _connection.Execute("DELETE FROM events WHERE id = @id;", new { id }, transaction) > 0;
}
