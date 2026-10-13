using Kado.Data;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Tests;

/// <summary>
/// 従来の保存先から <c>data</c> への写し。写して、確かめて、元は消さずに残す。
/// </summary>
public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kado-migrate-").FullName;

    private string Source => Path.Combine(_root, "source");

    private string Target => Path.Combine(_root, "exe", "data");

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 30, 0, TimeSpan.FromHours(9));

    public DataMigrationTests() => Directory.CreateDirectory(Path.Combine(_root, "exe"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けで転んでもテストの結果は変えない
        }
    }

    [Fact]
    public void WALに残っている分も取りこぼさずに写す()
    {
        Directory.CreateDirectory(Source);
        var db = Path.Combine(Source, "data.db");

        // WAL のまま接続を開いておく。チェックポイントされる前の変更は -wal にしか無い
        using var live = DataLocationTests.Open(db);
        DataLocationTests.Execute(live, "PRAGMA journal_mode = WAL;");
        DataLocationTests.Execute(live, "PRAGMA wal_autocheckpoint = 0;");
        DataLocationTests.Execute(live, "CREATE TABLE t (title TEXT);");
        DataLocationTests.Execute(live, "INSERT INTO t VALUES ('WAL にだけある行');");
        Assert.True(File.Exists(db + "-wal"));
        Assert.True(new FileInfo(db + "-wal").Length > 0);

        var result = DataMigration.Copy(Source, Target, Now);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("WAL にだけある行", DataLocationTests.ReadTitle(Path.Combine(Target, "data.db")));
    }

    [Fact]
    public void データベース以外はそのまま写しupdatesは写さない()
    {
        DataLocationTests.MakeData(Source, "予定");
        File.WriteAllText(Path.Combine(Source, "google-tokens.dat"), "トークン");
        File.WriteAllText(Path.Combine(Source, "google-client.json"), "{}");
        File.WriteAllText(Path.Combine(Source, "shell.log"), "ログ");
        Directory.CreateDirectory(Path.Combine(Source, "backups"));
        File.WriteAllText(Path.Combine(Source, "backups", "data-1.db"), "控え");
        Directory.CreateDirectory(Path.Combine(Source, "updates"));
        File.WriteAllText(Path.Combine(Source, "updates", "Kado-v9.exe"), "入れ替え待ち");

        var result = DataMigration.Copy(Source, Target, Now);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("トークン", File.ReadAllText(Path.Combine(Target, "google-tokens.dat")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Target, "google-client.json")));
        Assert.Equal("ログ", File.ReadAllText(Path.Combine(Target, "shell.log")));
        Assert.Equal("控え", File.ReadAllText(Path.Combine(Target, "backups", "data-1.db")));
        Assert.False(Directory.Exists(Path.Combine(Target, "updates")));
        // 移した印は写し先には持ち込まない
        Assert.False(File.Exists(Path.Combine(Target, DataMigration.MovedMarkerFileName)));
    }

    [Fact]
    public void 元は消さず移した日時と行き先を書いた印を置く()
    {
        DataLocationTests.MakeData(Source, "予定");
        File.WriteAllText(Path.Combine(Source, "shell.log"), "ログ");
        Directory.CreateDirectory(Path.Combine(Source, "updates"));
        File.WriteAllText(Path.Combine(Source, "updates", "Kado-v9.exe"), "入れ替え待ち");

        DataMigration.Copy(Source, Target, Now);

        Assert.Equal("予定", DataLocationTests.ReadTitle(Path.Combine(Source, "data.db")));
        Assert.Equal("ログ", File.ReadAllText(Path.Combine(Source, "shell.log")));
        Assert.Equal("入れ替え待ち", File.ReadAllText(Path.Combine(Source, "updates", "Kado-v9.exe")));

        var marker = File.ReadAllText(Path.Combine(Source, DataMigration.MovedMarkerFileName));
        Assert.Contains("2026-10-13 09:30", marker);
        Assert.Contains(Target, marker);
    }

    [Fact]
    public void 元のデータベースが無ければ失敗し何も残さない()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "shell.log"), "ログ");

        var result = DataMigration.Copy(Source, Target, Now);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(Target));
        Assert.False(Directory.Exists(Target + DataMigration.StagingSuffix));
        Assert.False(File.Exists(Path.Combine(Source, DataMigration.MovedMarkerFileName)));
    }

    [Fact]
    public void 前回の写しかけが残っていても捨ててやり直せる()
    {
        DataLocationTests.MakeData(Source, "予定");
        Directory.CreateDirectory(Target + DataMigration.StagingSuffix);
        File.WriteAllText(Path.Combine(Target + DataMigration.StagingSuffix, "古い写しかけ"), "x");

        var result = DataMigration.Copy(Source, Target, Now);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(Path.Combine(Target, "古い写しかけ")));
        Assert.False(Directory.Exists(Target + DataMigration.StagingSuffix));
    }

    [Fact]
    public void dataだけ先にあってもその中身を消さずに重ねる()
    {
        DataLocationTests.MakeData(Source, "予定");
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "shell.log"), "先にあったログ");
        File.WriteAllText(Path.Combine(Target, "別のファイル.txt"), "残る");

        var result = DataMigration.Copy(Source, Target, Now);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("予定", DataLocationTests.ReadTitle(Path.Combine(Target, "data.db")));
        Assert.Equal("残る", File.ReadAllText(Path.Combine(Target, "別のファイル.txt")));
    }
}
