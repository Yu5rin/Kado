using Dapper;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Repositories;

/// <summary>消した記録1件。</summary>
/// <param name="Id">ローカルの識別子。</param>
/// <param name="Kind"><c>event</c> か <c>task</c>。</param>
/// <param name="GoogleId">Google 側の識別子。未同期のまま消したなら null。</param>
/// <param name="DeletedAt">消した時刻。</param>
/// <param name="SourceId">
/// 持ち主。予定ならカレンダー、タスクならタスクリストの識別子。
/// <para>版を上げる前から残っている記録では null。</para>
/// </param>
public sealed record Tombstone(
    string Id, string Kind, string? GoogleId, DateTimeOffset DeletedAt, string? SourceId = null);

/// <summary>
/// 消したことの記録。
/// <para>
/// <b>消した記録を残さないと、次の同期で復活する。</b>こちらで消しただけでは、
/// 相手側にはまだ残っている。次に一覧を取りに行くと「こちらに無い予定」として
/// 降ってきて、消したはずのものが戻る。消した事実を覚えておき、相手側へ伝えるまで
/// 持っておく。
/// </para>
/// <para>
/// 伝え終わったら消す。残し続けると、同じ ID が再利用されたときに新しいほうまで
/// 消してしまう。
/// </para>
/// </summary>
public sealed class TombstoneRepository(SqliteConnection connection)
{
    /// <summary>予定を表す種別。</summary>
    public const string EventKind = "event";

    /// <summary>タスクを表す種別。</summary>
    public const string TaskKind = "task";

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// 消したことを記録する。同じものを二度消しても1件にまとまる。
    /// <para>
    /// <paramref name="sourceId"/> は<b>必ず渡す</b>。持ち主が分からないと、同期のときに
    /// 関係の無いカレンダーへ削除を投げてしまう。
    /// </para>
    /// </summary>
    public void Record(string id, string kind, string? googleId, DateTimeOffset deletedAt,
        string? sourceId = null, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        _connection.Execute(
            """
            INSERT INTO tombstones (id, kind, google_id, deleted_at, source_id)
            VALUES (@id, @kind, @googleId, @deletedAt, @sourceId)
            ON CONFLICT (id, kind) DO UPDATE SET
                google_id  = COALESCE(excluded.google_id, tombstones.google_id),
                deleted_at = excluded.deleted_at,
                source_id  = COALESCE(excluded.source_id, tombstones.source_id);
            """,
            new { id, kind, googleId, deletedAt = deletedAt.ToUnixTimeSeconds(), sourceId },
            transaction);
    }

    /// <summary>
    /// まだ伝えていない削除。
    /// <para>Google 側の識別子が無いものは伝えようがないので含めない。</para>
    /// <para>
    /// <paramref name="sourceId"/> を渡すと、<b>その持ち主のものだけ</b>返す。同期は
    /// カレンダーごとに回るので、絞らないと他所の予定まで投げてしまう。持ち主が分からない
    /// 古い記録は、どこのものか決められないので一緒に返す。
    /// </para>
    /// </summary>
    public IReadOnlyList<Tombstone> Pending(string kind, string? sourceId = null) =>
        _connection.Query<(string Id, string Kind, string? GoogleId, long DeletedAt, string? SourceId)>(
                """
                SELECT id, kind, google_id, deleted_at, source_id FROM tombstones
                WHERE kind = @kind AND google_id IS NOT NULL
                  AND (@sourceId IS NULL OR source_id IS NULL OR source_id = @sourceId)
                ORDER BY deleted_at;
                """,
                new { kind, sourceId })
            .Select(r => new Tombstone(
                r.Id, r.Kind, r.GoogleId, DateTimeOffset.FromUnixTimeSeconds(r.DeletedAt), r.SourceId))
            .ToArray();

    /// <summary>この識別子は消されたか。相手から降ってきたものを復活させないために見る。</summary>
    public bool Contains(string id, string kind) =>
        _connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM tombstones WHERE id = @id AND kind = @kind;",
            new { id, kind }) > 0;

    /// <summary>Google 側の識別子で引く。相手の一覧には Google の ID しか無い。</summary>
    public bool ContainsGoogleId(string googleId, string kind) =>
        _connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM tombstones WHERE google_id = @googleId AND kind = @kind;",
            new { googleId, kind }) > 0;

    /// <summary>伝え終わったので記録を消す。残し続けると ID の再利用で取り違える。</summary>
    public bool Clear(string id, string kind, SqliteTransaction? transaction = null) =>
        _connection.Execute(
            "DELETE FROM tombstones WHERE id = @id AND kind = @kind;",
            new { id, kind }, transaction) > 0;

    /// <summary>すべて消す。全再同期をかけるときに使う。</summary>
    public void ClearAll() => _connection.Execute("DELETE FROM tombstones;");

    /// <summary>
    /// もう無いカレンダー／タスクリストに向けた削除の記録を捨てる。
    /// <para>
    /// 相手ごと無くなったので、伝える先が無い。残しておくと同期のたびに
    /// 消えた相手へ投げ、そのたびに notFound で返ってくる。
    /// </para>
    /// </summary>
    /// <returns>捨てた記録の件数。</returns>
    public int ForgetSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        return _connection.Execute(
            "DELETE FROM tombstones WHERE source_id = @sourceId;", new { sourceId });
    }

    /// <summary>
    /// 古い記録を片付ける。
    /// <para>
    /// 伝えられないまま残ったもの（連携を外したあとに消した、など）が溜まり続けるのを防ぐ。
    /// </para>
    /// </summary>
    public int Prune(DateTimeOffset olderThan) =>
        _connection.Execute(
            "DELETE FROM tombstones WHERE deleted_at < @limit;",
            new { limit = olderThan.ToUnixTimeSeconds() });

    /// <summary>記録の件数。</summary>
    public int Count() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM tombstones;");
}
