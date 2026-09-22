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

        // 存在しないタスクに作業時間ブロックをぶら下げようとすると弾かれる
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
}
