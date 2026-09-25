using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Migrations;

namespace Kado.Data.Tests;

/// <summary>
/// V8 で足した索引が、狙った問い合わせで実際に使われることを
/// <c>EXPLAIN QUERY PLAN</c> で確かめる。
/// <para>
/// 「索引を足した」だけでは、SQLite がそれを選ぶとは限らない（式や部分索引は特に、
/// クエリの書き方と一致していないと使われない）。ここでは V8 を当てる前後で
/// 実行計画を取り、狙った索引名が現れるかどうかで確かめる。
/// </para>
/// </summary>
public class DatabaseIndexTests
{
    /// <summary>V7 までの状態（V8 の索引が無い）を作る。</summary>
    private static TestDatabase CreateAtV7()
    {
        var db = TestDatabase.CreateWithoutSchema();

        foreach (var migration in SchemaMigrations.All.Where(m => m.Version < 8).OrderBy(m => m.Version))
        {
            db.Connection.Execute(migration.Sql);
            db.Connection.Execute($"PRAGMA user_version = {migration.Version};");
        }

        return db;
    }

    /// <summary><c>EXPLAIN QUERY PLAN</c> の各行の <c>detail</c> 列（例: "SEARCH events USING INDEX …"）。</summary>
    private static IReadOnlyList<string> QueryPlan(
        SqliteConnection connection, string sql, object? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";

        if (parameters is not null)
        {
            foreach (var property in parameters.GetType().GetProperties())
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@" + property.Name;
                parameter.Value = property.GetValue(parameters) ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        var detailIndex = reader.GetOrdinal("detail");
        while (reader.Read()) lines.Add(reader.GetString(detailIndex));
        return lines;
    }

    private const string InRangeSql = """
        SELECT * FROM events
        WHERE recurrence IS NULL AND date <= @to AND COALESCE(end_date, date) >= @from;
        """;

    [Fact]
    public void V7までは期間検索がdateの索引だけで過去を全走査する()
    {
        using var db = CreateAtV7();

        var plan = QueryPlan(db.Connection, InRangeSql, new { from = "2026-09-01", to = "2026-09-30" });

        // date の索引（ix_events_date）は使うが、COALESCE(end_date, date) 側の絞り込みは
        // 索引を持たない。ix_events_span はまだ無い
        Assert.DoesNotContain(plan, line => line.Contains("ix_events_span", StringComparison.Ordinal));
    }

    [Fact]
    public void V8を当てると期間検索が式索引を使う()
    {
        using var db = CreateAtV7();
        DatabaseMigrator.Migrate(db.Connection);

        var plan = QueryPlan(db.Connection, InRangeSql, new { from = "2026-09-01", to = "2026-09-30" });

        Assert.Contains(plan, line => line.Contains("ix_events_span", StringComparison.Ordinal));
    }

    [Fact]
    public void V8を当てるとカレンダーIDの絞り込みが索引を使う()
    {
        using var db = CreateAtV7();

        var before = QueryPlan(
            db.Connection, "SELECT * FROM events WHERE calendar_id = @id;", new { id = "cal-a" });
        Assert.DoesNotContain(before, line => line.Contains("ix_events_calendar", StringComparison.Ordinal));

        DatabaseMigrator.Migrate(db.Connection);

        var after = QueryPlan(
            db.Connection, "SELECT * FROM events WHERE calendar_id = @id;", new { id = "cal-a" });
        Assert.Contains(after, line => line.Contains("ix_events_calendar", StringComparison.Ordinal));
    }

    [Fact]
    public void V8を当てるとタスクリストIDの絞り込みが索引を使う()
    {
        using var db = CreateAtV7();

        var before = QueryPlan(
            db.Connection, "SELECT * FROM tasks WHERE task_list_id = @id;", new { id = "list-a" });
        Assert.DoesNotContain(before, line => line.Contains("ix_tasks_task_list", StringComparison.Ordinal));

        DatabaseMigrator.Migrate(db.Connection);

        var after = QueryPlan(
            db.Connection, "SELECT * FROM tasks WHERE task_list_id = @id;", new { id = "list-a" });
        Assert.Contains(after, line => line.Contains("ix_tasks_task_list", StringComparison.Ordinal));
    }

    [Fact]
    public void V8を当てるとtombstoneのGoogleID突き合わせが索引を使う()
    {
        using var db = CreateAtV7();

        var before = QueryPlan(
            db.Connection,
            "SELECT COUNT(*) FROM tombstones WHERE google_id = @googleId AND kind = @kind;",
            new { googleId = "g1", kind = "event" });
        Assert.DoesNotContain(before, line => line.Contains("ix_tombstones_google_kind", StringComparison.Ordinal));

        DatabaseMigrator.Migrate(db.Connection);

        var after = QueryPlan(
            db.Connection,
            "SELECT COUNT(*) FROM tombstones WHERE google_id = @googleId AND kind = @kind;",
            new { googleId = "g1", kind = "event" });
        Assert.Contains(after, line => line.Contains("ix_tombstones_google_kind", StringComparison.Ordinal));
    }

    /// <summary>
    /// 索引を足しても、EventRepository.InRange の結果そのものは変わらない
    /// （複数日の予定・終了日なしの予定・境界の日）ことを、V8 適用後の実データで確かめる。
    /// <para>個々の境界条件は RepositoryTests 側で既に固めてある。ここでは V8 適用後も
    /// 同じ経路（DatabaseMigrator.Migrate 済みの接続）で動くことだけを見る。</para>
    /// </summary>
    [Fact]
    public void V8を当てても期間検索の結果は変わらない()
    {
        using var db = TestDatabase.Create(); // 最新版（V8 込み）
        var repo = new Repositories.EventRepository(db.Connection);

        repo.UpsertMany([
            new() { Id = "before", Title = "前", Date = new DateOnly(2026, 9, 20), UpdatedAt = DateTimeOffset.UnixEpoch },
            new() { Id = "multi", Title = "複数日", Date = new DateOnly(2026, 9, 20), EndDate = new DateOnly(2026, 9, 25), UpdatedAt = DateTimeOffset.UnixEpoch },
            new() { Id = "noend", Title = "終了日なし", Date = new DateOnly(2026, 9, 24), UpdatedAt = DateTimeOffset.UnixEpoch },
            new() { Id = "after", Title = "後", Date = new DateOnly(2026, 9, 30), UpdatedAt = DateTimeOffset.UnixEpoch },
        ]);

        var found = repo.InRange(new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 24))
            .Select(e => e.Id)
            .ToHashSet();

        Assert.Equal(new HashSet<string> { "multi", "noend" }, found);
    }
}
