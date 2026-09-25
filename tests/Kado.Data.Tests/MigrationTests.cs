using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Migrations;

namespace Kado.Data.Tests;

public class MigrationTests
{
    [Fact]
    public void 空のデータベースの版は0()
    {
        using var db = TestDatabase.CreateWithoutSchema();
        Assert.Equal(0, DatabaseMigrator.GetVersion(db.Connection));
    }

    [Fact]
    public void 適用すると最新の版になる()
    {
        using var db = TestDatabase.CreateWithoutSchema();

        var applied = DatabaseMigrator.Migrate(db.Connection);

        Assert.NotEmpty(applied);
        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetVersion(db.Connection));
    }

    [Fact]
    public void 二度適用しても何も起きない()
    {
        using var db = TestDatabase.CreateWithoutSchema();

        DatabaseMigrator.Migrate(db.Connection);
        var second = DatabaseMigrator.Migrate(db.Connection);

        Assert.Empty(second);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("tasks")]
    // work_blocks（作業時間ブロック）は実装しないと決まったが、テーブル定義は
    // SchemaMigrations の V1 に残したままにしてある。このテストも「残っていること」
    // が仕様になったので、そのまま残す
    [InlineData("work_blocks")]
    [InlineData("working_days")]
    [InlineData("data_ranges")]
    [InlineData("milestones")]
    [InlineData("settings")]
    [InlineData("sync_state")]
    [InlineData("tombstones")]
    public void 必要なテーブルが作られる(string table)
    {
        using var db = TestDatabase.Create();

        var found = db.Connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table;",
            new { table });

        Assert.Equal(1, found);
    }

    [Fact]
    public void 版番号は重複しない()
    {
        var versions = SchemaMigrations.All.Select(m => m.Version).ToArray();
        Assert.Equal(versions.Length, versions.Distinct().Count());
    }

    [Fact]
    public void 未来の版のデータベースは開かない()
    {
        using var db = TestDatabase.CreateWithoutSchema();
        db.Connection.Execute($"PRAGMA user_version = {SchemaMigrations.LatestVersion + 1};");

        // 知らないスキーマに書き込むと壊すので、黙って動かさず止める
        var e = Assert.Throws<InvalidOperationException>(() => DatabaseMigrator.Migrate(db.Connection));
        Assert.Contains("新しいバージョンのアプリ", e.Message);
    }

    [Fact]
    public void 外部キー制約が効く()
    {
        using var db = TestDatabase.Create();

        // 存在しないタスクに作業時間ブロックをぶら下げようとすると弾かれる。
        // work_blocks は実装しないと決まったが、テーブル定義（外部キーを含む）は
        // 残っているので、この制約もそのまま生きている
        Assert.Throws<SqliteException>(() => db.Connection.Execute(
            """
            INSERT INTO work_blocks (id, task_id, date, start_time, duration_minutes)
            VALUES ('b1', 'いないタスク', '2026-09-24', '09:00', 60);
            """));
    }

    /// <summary>
    /// V6（タスクの並び順と作成日時）が、V5 までしか当たっていない既存のデータベースにも
    /// 当たり、既存行の <c>created_at</c> が <c>updated_at</c> で埋まることを確かめる。
    /// </summary>
    [Fact]
    public void V6は既存のタスクのcreated_atをupdated_atで埋める()
    {
        using var db = TestDatabase.CreateWithoutSchema();

        // V6 より前の状態を再現する
        foreach (var migration in SchemaMigrations.All.Where(m => m.Version < 6).OrderBy(m => m.Version))
        {
            db.Connection.Execute(migration.Sql);
            db.Connection.Execute($"PRAGMA user_version = {migration.Version};");
        }

        db.Connection.Execute(
            "INSERT INTO tasks (id, title, updated_at) VALUES ('t1', '既存のタスク', 1758500000000);");

        var applied = DatabaseMigrator.Migrate(db.Connection);

        Assert.Contains(applied, m => m.Version == 6);
        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetVersion(db.Connection));

        Assert.Equal(
            1758500000000L,
            db.Connection.ExecuteScalar<long>("SELECT created_at FROM tasks WHERE id = 't1';"));
        Assert.Equal(
            0L,
            db.Connection.ExecuteScalar<long>("SELECT sort_order FROM tasks WHERE id = 't1';"));
    }

    /// <summary>
    /// V7（予定に Google 側の実カレンダーを持つ）が、既存の Google 連携済みの予定の
    /// <c>google_calendar_id</c> を、いま入っているカレンダー（<c>calendar_id</c>）で
    /// 埋めることを確かめる。埋めないと、更新後に最初にカレンダーを変えたときの判定
    /// （events.move すべきか）が全部「移した」側に倒れ、直したかった不具合が
    /// そのまま再発する。
    /// </summary>
    [Fact]
    public void V7は既存のGoogle連携済みの予定のgoogle_calendar_idを埋める()
    {
        using var db = TestDatabase.CreateWithoutSchema();

        // V6 までの状態を再現する
        foreach (var migration in SchemaMigrations.All.Where(m => m.Version < 7).OrderBy(m => m.Version))
        {
            db.Connection.Execute(migration.Sql);
            db.Connection.Execute($"PRAGMA user_version = {migration.Version};");
        }

        db.Connection.Execute(
            "INSERT INTO calendars (id, summary, updated_at) VALUES ('cal-a', '仕事', 1758500000000);");

        // Google と結び付いている予定。対応するカレンダーが calendars 表にある
        db.Connection.Execute(
            """
            INSERT INTO events (id, title, date, calendar_id, google_event_id, updated_at)
            VALUES ('e1', '定例', '2026-09-24', 'cal-a', 'g1', 1758500000000);
            """);

        // Google と結び付いていない（ローカルだけの）予定。埋めてはいけない
        db.Connection.Execute(
            """
            INSERT INTO events (id, title, date, calendar_id, updated_at)
            VALUES ('e2', '私用の予定', '2026-09-24', 'local:mycal', 1758500000000);
            """);

        // 結び付いているが、対応するカレンダーがもう calendars 表に無い（消えた・旧データ）。
        // 何が正しい場所か分からないので、無理に埋めない
        db.Connection.Execute(
            """
            INSERT INTO events (id, title, date, calendar_id, google_event_id, updated_at)
            VALUES ('e3', '孤立した予定', '2026-09-24', 'cal-removed', 'g3', 1758500000000);
            """);

        DatabaseMigrator.Migrate(db.Connection);

        Assert.Equal(
            "cal-a",
            db.Connection.ExecuteScalar<string>("SELECT google_calendar_id FROM events WHERE id = 'e1';"));
        Assert.Null(
            db.Connection.ExecuteScalar<string?>("SELECT google_calendar_id FROM events WHERE id = 'e2';"));
        Assert.Null(
            db.Connection.ExecuteScalar<string?>("SELECT google_calendar_id FROM events WHERE id = 'e3';"));
    }
}
