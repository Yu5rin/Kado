using Kado.Data;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Tests;

/// <summary>
/// 起動時にデータベースを開けなかったときの見分け。
/// <para>
/// <b>一時的なロックを「壊れている」扱いにして、健全なデータベースを退避させない。</b>
/// 「どけて新しく始める」を勧めるのは、本当に壊れているときと、新しい版のデータのときだけ。
/// </para>
/// </summary>
public class DatabaseOpenFailureTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void ロックは使用中として退避を勧めない(int code)
    {
        var kind = DatabaseOpenFailure.Classify(new SqliteException("database is locked", code));

        Assert.Equal(DatabaseOpenFailureKind.InUse, kind);
        Assert.False(DatabaseOpenFailure.OffersSetAside(kind));
        Assert.True(DatabaseOpenFailure.OffersRetry(kind));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public void 本当の破損だけが今までの流れ(int code)
    {
        var kind = DatabaseOpenFailure.Classify(new SqliteException("malformed", code));

        Assert.Equal(DatabaseOpenFailureKind.Corrupt, kind);
        Assert.True(DatabaseOpenFailure.OffersSetAside(kind));
        Assert.False(DatabaseOpenFailure.OffersRetry(kind));
    }

    [Fact]
    public void 新しい版のデータは退避を選べる()
    {
        var kind = DatabaseOpenFailure.Classify(new InvalidOperationException("スキーマ版が新しい"));

        Assert.Equal(DatabaseOpenFailureKind.NewerVersion, kind);
        Assert.True(DatabaseOpenFailure.OffersSetAside(kind));
    }

    [Fact]
    public void 書き込めない保存先は権限の案内で退避を勧めない()
    {
        var kind = DatabaseOpenFailure.Classify(new UnauthorizedAccessException("denied"));

        Assert.Equal(DatabaseOpenFailureKind.NoAccess, kind);
        Assert.False(DatabaseOpenFailure.OffersSetAside(kind));

        var message = DatabaseOpenFailure.Describe(kind, @"C:\Users\x\AppData\Local\Kado\data.db");
        Assert.Contains("書き込む権限", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\x\AppData\Local\Kado\data.db", message, StringComparison.Ordinal);
        Assert.Contains("壊れていません", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 共有違反は使用中()
    {
        var sharing = new IOException("used by another process", unchecked((int)0x80070020));

        Assert.Equal(DatabaseOpenFailureKind.InUse, DatabaseOpenFailure.Classify(sharing));
        Assert.Equal(DatabaseOpenFailureKind.NoAccess, DatabaseOpenFailure.Classify(new IOException("x")));
    }

    [Fact]
    public void ディスクがいっぱいなら空きを案内する()
    {
        var kind = DatabaseOpenFailure.Classify(new SqliteException("full", 13));

        Assert.Equal(DatabaseOpenFailureKind.DiskFull, kind);
        Assert.Contains("空き", DatabaseOpenFailure.Describe(kind, "data.db"), StringComparison.Ordinal);
        Assert.False(DatabaseOpenFailure.OffersSetAside(kind));
    }

    [Fact]
    public void 想定外の失敗は退避を勧めない()
    {
        var kind = DatabaseOpenFailure.Classify(new SqliteException("SQL logic error", 1));

        Assert.Equal(DatabaseOpenFailureKind.Other, kind);
        Assert.False(DatabaseOpenFailure.OffersSetAside(kind));
    }

    [Fact]
    public void 使用中の文言は壊れていないと伝える()
    {
        var message = DatabaseOpenFailure.Describe(DatabaseOpenFailureKind.InUse, "data.db");

        Assert.Contains("壊れていません", message, StringComparison.Ordinal);
        Assert.DoesNotContain("壊れたデータをどけ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 実際にロックされたデータベースは使用中として見分けられる()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kado-lock-{Guid.NewGuid():N}.db");

        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };

            using var holder = new SqliteConnection(builder.ToString());
            holder.Open();
            using (var create = holder.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t(x); BEGIN EXCLUSIVE;";
                create.ExecuteNonQuery();
            }

            // 別の接続が、1秒だけ待って、それでも読めずに断られる
            var other = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 };
            using var second = new SqliteConnection(other.ToString());
            second.Open();
            using var read = second.CreateCommand();
            read.CommandText = "SELECT count(*) FROM t;";
            var failure = Assert.Throws<SqliteException>(() => read.ExecuteScalar());

            Assert.Equal(DatabaseOpenFailureKind.InUse, DatabaseOpenFailure.Classify(failure));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
