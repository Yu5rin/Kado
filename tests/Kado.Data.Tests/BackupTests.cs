using Kado.Data;
using Kado.Data.Backup;
using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// バックアップと復元。ファイルを扱うので一時ディレクトリを使う。
/// </summary>
public class BackupTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("slideina-backup-").FullName;

    private static CalendarEvent Sample(string id) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = new DateOnly(2026, 9, 24),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void 書き出して読み戻せる()
    {
        var databasePath = Path.Combine(_work, "data.db");
        var backupPath = Path.Combine(_work, "backup.db");

        // 元のデータベースに1件入れて書き出す
        var database = CalendarDatabase.OpenFile(databasePath);
        using (var connection = database.ConnectAndMigrate())
        {
            new EventRepository(connection).Upsert(Sample("e1"));
            DatabaseBackup.SaveTo(connection, backupPath);
        }

        Assert.True(File.Exists(backupPath));

        // 書き出したファイルを開くと、同じ内容が入っている
        using (var restored = CalendarDatabase.OpenFile(backupPath).Connect())
        {
            Assert.Equal("予定e1", new EventRepository(restored).Find("e1")!.Title);
        }
    }

    [Fact]
    public void 書き出し先が既にあっても上書きできる()
    {
        var backupPath = Path.Combine(_work, "backup.db");
        File.WriteAllText(backupPath, "古い中身");

        using var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        DatabaseBackup.SaveTo(connection, backupPath);

        // VACUUM INTO は書き出し先があると失敗するので、消してから書いている
        using var restored = CalendarDatabase.OpenFile(backupPath).Connect();
        Assert.Equal(0, new EventRepository(restored).Count());
    }

    [Fact]
    public void 日時つきの名前で書き出せる()
    {
        using var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

        var path = DatabaseBackup.SaveToDirectory(
            connection, _work, new DateTimeOffset(2026, 9, 24, 13, 5, 30, TimeSpan.Zero));

        Assert.Equal("data-20260924-130530.db", Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 復元すると内容が入れ替わる()
    {
        var databasePath = Path.Combine(_work, "data.db");
        var backupPath = Path.Combine(_work, "backup.db");

        // 1件の状態でバックアップを取る
        var database = CalendarDatabase.OpenFile(databasePath);
        using (var connection = database.ConnectAndMigrate())
        {
            new EventRepository(connection).Upsert(Sample("e1"));
            DatabaseBackup.SaveTo(connection, backupPath);

            // そのあとで2件目を足す
            new EventRepository(connection).Upsert(Sample("e2"));
            Assert.Equal(2, new EventRepository(connection).Count());
        }

        // 接続を閉じてから差し替える
        var rescued = DatabaseBackup.RestoreFrom(backupPath, databasePath);

        using (var connection = CalendarDatabase.OpenFile(databasePath).Connect())
        {
            // バックアップ時点に戻り、2件目は消えている
            Assert.Equal(1, new EventRepository(connection).Count());
            Assert.Null(new EventRepository(connection).Find("e2"));
        }

        // 差し替え前の内容も残っているので、復元をやり直せる
        Assert.NotNull(rescued);
        Assert.True(File.Exists(rescued));
    }

    [Fact]
    public void 復元元が無ければ失敗する()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DatabaseBackup.RestoreFrom(Path.Combine(_work, "いないファイル.db"),
                                       Path.Combine(_work, "data.db")));
    }

    [Fact]
    public void 書き出しに失敗しても既にあるバックアップは壊さない()
    {
        // 閉じた接続からは書き出せない。以前は書き出し先を先に消していたので、良いバックアップまで失った
        var backupPath = Path.Combine(_work, "backup.db");
        File.WriteAllText(backupPath, "良いバックアップの中身");

        var closed = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        closed.Dispose();

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.SaveTo(closed, backupPath));

        Assert.Equal("良いバックアップの中身", File.ReadAllText(backupPath));
        Assert.Empty(Directory.GetFiles(_work, "*.writing*"));
    }

    [Fact]
    public void 書き出し先がフォルダなら失敗して一時ファイルを残さない()
    {
        var destination = Path.Combine(_work, "folder.db");
        Directory.CreateDirectory(destination);

        using var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.SaveTo(connection, destination));

        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(_work, "*.writing*"));
    }

    [Fact]
    public void 読めないファイルでの復元はいまのデータに触れずに断る()
    {
        var databasePath = Path.Combine(_work, "data.db");
        var garbage = Path.Combine(_work, "garbage.db");
        File.WriteAllText(garbage, new string('x', 8192));

        using (var connection = CalendarDatabase.OpenFile(databasePath).ConnectAndMigrate())
        {
            new EventRepository(connection).Upsert(Sample("e1"));
        }

        // 接続のプールを手放して、WAL を本体へ反映させてから比べる（RestoreFrom も同じことをする）
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);

        var failure = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => DatabaseBackup.RestoreFrom(garbage, databasePath));

        // SQLite のファイルではないという失敗で、本体にも .bak にも .restoring にも触れていない
        Assert.True(SqliteFailure.IsCorrupt(failure));
        Assert.Equal(before, File.ReadAllBytes(databasePath));
        Assert.False(File.Exists(databasePath + ".bak"));
        Assert.False(File.Exists(databasePath + ".restoring"));
    }

    [Fact]
    public void 復元の途中で失敗しても本体は元のまま()
    {
        // 本体の隣に、復元の作業用の名前でフォルダがあると、コピーに失敗する
        var databasePath = Path.Combine(_work, "data.db");
        var backupPath = Path.Combine(_work, "backup.db");

        using (var connection = CalendarDatabase.OpenFile(databasePath).ConnectAndMigrate())
        {
            new EventRepository(connection).Upsert(Sample("e1"));
            DatabaseBackup.SaveTo(connection, backupPath);
            new EventRepository(connection).Upsert(Sample("e2"));
        }

        Directory.CreateDirectory(databasePath + ".restoring");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);

        Assert.ThrowsAny<Exception>(() => DatabaseBackup.RestoreFrom(backupPath, databasePath));

        Assert.Equal(before, File.ReadAllBytes(databasePath));
    }

    [Fact]
    public void 復元元の確認は壊れていないデータベースを通す()
    {
        var backupPath = Path.Combine(_work, "backup.db");

        using (var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate())
        {
            DatabaseBackup.SaveTo(connection, backupPath);
        }

        DatabaseBackup.EnsureReadableDatabase(backupPath);
    }

    [Fact]
    public void 小さすぎるファイルはデータベースとして読めない()
    {
        var path = Path.Combine(_work, "tiny.db");
        File.WriteAllText(path, "x");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => DatabaseBackup.EnsureReadableDatabase(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { /* 後始末に失敗してもテストの結果には影響しない */ }
    }
}
