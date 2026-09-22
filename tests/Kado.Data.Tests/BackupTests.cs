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

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { /* 後始末に失敗してもテストの結果には影響しない */ }
    }
}
