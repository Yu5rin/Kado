using Microsoft.Data.Sqlite;
using Kado.Data;

namespace Kado.Data.Tests;

/// <summary>
/// テスト1件ごとに使い捨てるデータベース。
/// <para>
/// メモリ上に作るのでファイルを残さず、テストどうしも干渉しない。
/// 接続を閉じると消えるため、後始末も要らない。
/// </para>
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    public CalendarDatabase Database { get; }
    public SqliteConnection Connection { get; }

    private TestDatabase(CalendarDatabase database, SqliteConnection connection)
    {
        Database = database;
        Connection = connection;
    }

    /// <summary>スキーマを最新まで進めたデータベースを作る。</summary>
    public static TestDatabase Create()
    {
        var database = CalendarDatabase.OpenInMemory();
        return new TestDatabase(database, database.ConnectAndMigrate());
    }

    /// <summary>スキーマを作らずに開く。マイグレーション自体を試すとき用。</summary>
    public static TestDatabase CreateWithoutSchema()
    {
        var database = CalendarDatabase.OpenInMemory();
        return new TestDatabase(database, database.Connect());
    }

    /// <summary>匿名化した旧バックアップを開く。</summary>
    public static Stream OpenLegacyBackup() =>
        File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "inaCalendar-backup-sample.json"));

    public void Dispose() => Connection.Dispose();
}
