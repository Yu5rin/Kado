using Kado.Data;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Tests;

/// <summary>
/// SQLite の失敗の見分け。<b>「ロックされている」と「壊れている」を取り違えない。</b>
/// 前者を壊れた扱いにすると、健全なデータベースを退避させてしまう。
/// </summary>
public class SqliteFailureTests
{
    private static SqliteException Failure(int code) => new("test", code);

    [Theory]
    [InlineData(5, SqliteFailureKind.Busy)]
    [InlineData(6, SqliteFailureKind.Busy)]
    [InlineData(11, SqliteFailureKind.Corrupt)]
    [InlineData(26, SqliteFailureKind.Corrupt)]
    [InlineData(13, SqliteFailureKind.DiskFull)]
    [InlineData(8, SqliteFailureKind.ReadOnly)]
    [InlineData(3, SqliteFailureKind.ReadOnly)]
    [InlineData(10, SqliteFailureKind.Io)]
    [InlineData(14, SqliteFailureKind.Io)]
    [InlineData(1, SqliteFailureKind.Other)]
    [InlineData(19, SqliteFailureKind.Other)]
    public void 結果コードから種類を決める(int code, SqliteFailureKind expected)
    {
        Assert.Equal(expected, SqliteFailure.Classify(Failure(code)));
    }

    [Fact]
    public void 拡張コードは下位8ビットで見る()
    {
        // SQLITE_BUSY_SNAPSHOT = 5 | (2 << 8)、SQLITE_IOERR_WRITE = 10 | (3 << 8)
        Assert.Equal(SqliteFailureKind.Busy, SqliteFailure.Classify(Failure(5 | (2 << 8))));
        Assert.Equal(SqliteFailureKind.Io, SqliteFailure.Classify(Failure(10 | (3 << 8))));
    }

    [Fact]
    public void ロックは壊れているではない()
    {
        Assert.True(SqliteFailure.IsBusy(Failure(5)));
        Assert.False(SqliteFailure.IsCorrupt(Failure(5)));
        Assert.True(SqliteFailure.IsCorrupt(Failure(11)));
        Assert.True(SqliteFailure.IsCorrupt(Failure(26)));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(13, true)]
    [InlineData(8, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(1, false)]
    public void 案内して続けてよいのは利用者が直せる種類だけ(int code, bool expected)
    {
        Assert.Equal(expected, SqliteFailure.IsRecoverable(Failure(code)));
    }

    [Theory]
    [InlineData(5, "ほかのアプリ")]
    [InlineData(13, "ディスクの空き")]
    [InlineData(8, "書き込めません")]
    [InlineData(11, "壊れている")]
    [InlineData(1, "ディスクの空き・ほかのアプリの使用を確かめてください")]
    public void 保存できなかったときの文言は次の一手が分かる(int code, string expectedPart)
    {
        var message = SqliteFailure.DescribeSaveFailure(Failure(code));

        Assert.StartsWith("保存できませんでした", message, StringComparison.Ordinal);
        Assert.Contains(expectedPart, message, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteException", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 実際のSQLiteの失敗も見分けられる()
    {
        // 存在しないファイルを読み取り専用で開くと、CANTOPEN になる
        var path = Path.Combine(Path.GetTempPath(), $"kado-none-{Guid.NewGuid():N}", "x.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };

        using var connection = new SqliteConnection(builder.ToString());
        var failure = Assert.Throws<SqliteException>(connection.Open);

        Assert.Equal(SqliteFailureKind.Io, SqliteFailure.Classify(failure));
    }

    [Fact]
    public void SQLiteのファイルではないものはNOTADBになる()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kado-garbage-{Guid.NewGuid():N}.db");
        File.WriteAllText(path, new string('x', 4096));

        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            var failure = Assert.Throws<SqliteException>(() => command.ExecuteScalar());

            Assert.True(SqliteFailure.IsCorrupt(failure));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
