using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Models;

namespace Kado.Data.Repositories;

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
        status AS Status, source_title AS SourceTitle, google_raw AS GoogleRaw,
        google_event_id AS GoogleEventId, google_calendar_id AS GoogleCalendarId,
        google_updated AS GoogleUpdated, google_missing AS GoogleMissing,
        source AS Source, notify AS Notify, pending_attachments AS PendingAttachments,
        updated_at AS UpdatedAt
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
    /// 一覧そのものは <c>calendars</c> の表が持つ。こちらは、表に載っていない所属を
    /// 見つけて起こすために使う（<c>CalendarWorkspace.EnsureSources</c>）。
    /// 旧データからの移行や、表を作る前に入った予定を拾う。
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
    /// 所属カレンダーを持たない予定を、指定のカレンダーへ入れる。
    /// <para>
    /// 所属が無いと左パネルに受け皿が無く、<b>チェックを外しても消せない</b>。実機で
    /// 全部のチェックを外しても一部の予定が残り、操作が効かないように見えた。
    /// </para>
    /// <para>
    /// 入れ先には必ず<b>このアプリの中だけのカレンダー</b>を渡すこと。Google のものへ
    /// 入れると、次の同期で勝手に相手へ送られてしまう。
    /// </para>
    /// </summary>
    /// <returns>入れ直した件数。</returns>
    public int AdoptOrphans(string calendarId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);

        return _connection.Execute(
            """
            UPDATE events SET calendar_id = @calendarId
            WHERE calendar_id IS NULL OR calendar_id = '';
            """,
            new { calendarId });
    }

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

    /// <summary>
    /// 指定のカレンダーに入っている予定だけ。
    /// <para>
    /// <c>All()</c> を呼んでから絞るより、DB 側で絞ったほうが速い
    /// （<c>ix_events_calendar</c>）。同期がカレンダーごとに回るとき、送る対象を
    /// 選ぶのに使う（<c>EventSyncEngine.PushChangesAsync</c>）。
    /// </para>
    /// </summary>
    public IReadOnlyList<CalendarEvent> ByCalendarId(string calendarId) =>
        _connection.Query<CalendarEvent>(
            $"SELECT {Columns} FROM events WHERE calendar_id = @calendarId ORDER BY date, start_time;",
            new { calendarId }).ToArray();

    /// <summary>
    /// 指定のカレンダーのどれかに入っている予定だけ。並びは <see cref="All"/> と同じ
    /// （日付・開始時刻の順）で、<c>All().Where(所属が ids にある)</c> と同じ結果になる。
    /// <para>
    /// 全件を読んで絞る代わりに、DB 側で絞る（<c>ix_events_calendar</c>）。実働日の組み立て
    /// （<c>CalendarWorkspace.ReloadWorkingDays</c>）は、「Kado」の印の予定しか要らないのに、
    /// 同期で変更があるたびに全予定を画面のスレッドで読んでいた。
    /// </para>
    /// </summary>
    public IReadOnlyList<CalendarEvent> ByCalendarIds(IReadOnlyCollection<string> calendarIds)
    {
        ArgumentNullException.ThrowIfNull(calendarIds);

        var ids = calendarIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        // SQLite の変数の数の上限に近いほど多いことは無い（カレンダーは数個〜数十個）が、
        // 超えるなら諦めて全件から絞る。結果は同じ
        if (ids.Length > MaxIdsPerQuery)
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            return All().Where(e => e.CalendarId is { } id && set.Contains(id)).ToArray();
        }

        return _connection.Query<CalendarEvent>(
            $"SELECT {Columns} FROM events WHERE calendar_id IN @ids ORDER BY date, start_time;",
            new { ids }).ToArray();
    }

    private const int MaxIdsPerQuery = 500;

    /// <summary><see cref="SearchText"/> の絞り込み。エスケープの文字は <see cref="TextSearch.LikeEscape"/>。</summary>
    private const string SearchWhere =
        "WHERE title LIKE @pattern ESCAPE '\\' OR location LIKE @pattern ESCAPE '\\' OR note LIKE @pattern ESCAPE '\\'";

    /// <summary>
    /// 題・場所・メモのどれかに <paramref name="text"/> を含む予定。検索（<c>MainViewModel.RunSearch</c>）が使う。
    /// <para>
    /// 全件を読んで絞らず、SQL の <c>LIKE</c> で先に絞る（<see cref="TextSearch"/>）。<b>読む列は
    /// 検索結果に要る分だけ</b>で、<c>google_raw</c> など大きな列は読まない。返す予定は
    /// <c>Id・Title・Date・Location・Note・Source</c> しか入っていない（ほかは既定値）。
    /// </para>
    /// <para>
    /// 並びは <see cref="All"/> と同じ（日付・開始時刻の順。同じなら登録順）。大文字小文字は区別せず、
    /// 全角と半角は別の文字（今までの探し方と同じ）。
    /// </para>
    /// </summary>
    public IReadOnlyList<CalendarEvent> SearchText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0) return [];

        var like = TextSearch.TryCreateLikePattern(text, out var pattern);

        var found = _connection.Query<CalendarEvent>(
            $"""
            SELECT id AS Id, title AS Title, date AS Date, location AS Location, note AS Note, source AS Source
            FROM events
            {(like ? SearchWhere : "")}
            ORDER BY date, start_time, rowid;
            """,
            new { pattern });

        // LIKE は先に絞るだけ。最終的な判定は、今までと同じ比べ方で行う
        return found
            .Where(e => TextSearch.Contains(e.Title, text)
                || TextSearch.Contains(e.Location, text)
                || TextSearch.Contains(e.Note, text))
            .ToArray();
    }

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
                status, source_title, google_raw,
                google_event_id, google_calendar_id, google_updated, google_missing, source, notify,
                pending_attachments, updated_at
            ) VALUES (
                @Id, @Title, @Date, @EndDate, @StartTime, @EndTime,
                @Location, @Note, @Color, @CalendarId, @Recurrence, @Url,
                @Status, @SourceTitle, @GoogleRaw,
                @GoogleEventId, @GoogleCalendarId, @GoogleUpdated, @GoogleMissing, @Source, @Notify,
                @PendingAttachments, @UpdatedAt
            )
            ON CONFLICT (id) DO UPDATE SET
                title = excluded.title, date = excluded.date, end_date = excluded.end_date,
                start_time = excluded.start_time, end_time = excluded.end_time,
                location = excluded.location, note = excluded.note, color = excluded.color,
                calendar_id = excluded.calendar_id, recurrence = excluded.recurrence,
                url = excluded.url, status = excluded.status,
                source_title = excluded.source_title, google_raw = excluded.google_raw,
                google_event_id = excluded.google_event_id,
                google_calendar_id = excluded.google_calendar_id,
                google_updated = excluded.google_updated,
                google_missing = excluded.google_missing,
                source = excluded.source, notify = excluded.notify,
                pending_attachments = excluded.pending_attachments,
                updated_at = excluded.updated_at;
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
