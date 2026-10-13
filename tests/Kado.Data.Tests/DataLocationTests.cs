using Kado.Data;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Tests;

/// <summary>
/// 保存先の決め方と、初回の引っ越し。
/// <para>
/// 既定は exe と同じフォルダの <c>data</c>。書けないときと、引っ越しに失敗したときは、
/// 従来の <c>%LOCALAPPDATA%\Kado</c> を使い続ける。いずれも<b>元のデータには触れない</b>。
/// </para>
/// </summary>
public sealed class DataLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kado-location-").FullName;

    private string Exe => Path.Combine(_root, "exe");

    private string LocalAppData => Path.Combine(_root, "local");

    private string Beside => Path.Combine(Exe, "data");

    private string Local => Path.Combine(LocalAppData, "Kado");

    private string Legacy => Path.Combine(LocalAppData, "SlideinaCalendar");

    public DataLocationTests()
    {
        Directory.CreateDirectory(Exe);
        Directory.CreateDirectory(LocalAppData);
    }

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

    // ------------------------------------------------------------------
    // 判断（純粋な関数）
    // ------------------------------------------------------------------

    [Theory]
    // 書き込めるか, data にある, 従来の Kado にある, 旧い名前にある → 答え
    [InlineData(true, false, false, false, DataPlacement.UseBesideExe)]
    [InlineData(true, true, false, false, DataPlacement.UseBesideExe)]
    [InlineData(true, true, true, false, DataPlacement.UseBesideExe)]
    [InlineData(true, true, true, true, DataPlacement.UseBesideExe)]
    [InlineData(true, false, true, false, DataPlacement.MigrateFromLocalAppData)]
    [InlineData(true, false, true, true, DataPlacement.MigrateFromLocalAppData)]
    [InlineData(true, false, false, true, DataPlacement.MigrateFromLegacy)]
    [InlineData(false, false, false, false, DataPlacement.UseLocalAppData)]
    [InlineData(false, true, false, false, DataPlacement.UseLocalAppData)]
    [InlineData(false, false, true, false, DataPlacement.UseLocalAppData)]
    [InlineData(false, false, false, true, DataPlacement.UseLocalAppData)]
    public void どこを使うかは引数だけで決まる(
        bool writable, bool beside, bool local, bool legacy, DataPlacement expected)
    {
        Assert.Equal(expected, DataLocation.Decide(writable, beside, local, legacy));
    }

    [Fact]
    public void exeのフォルダはProcessPathから取る()
    {
        Assert.Equal(
            Path.Combine("C", "Apps", "Kado"),
            DataLocation.ExeDirectory(Path.Combine("C", "Apps", "Kado", "Kado.exe"), "/unused/"));
    }

    [Fact]
    public void ProcessPathが無ければ基準フォルダを使う()
    {
        var baseDirectory = Path.Combine("C", "Apps", "Kado") + Path.DirectorySeparatorChar;

        Assert.Equal(Path.Combine("C", "Apps", "Kado"), DataLocation.ExeDirectory(null, baseDirectory));
        Assert.Equal(Path.Combine("C", "Apps", "Kado"), DataLocation.ExeDirectory("", baseDirectory));
    }

    [Fact]
    public void dotnetで動かしているときはdotnet本体のフォルダを使わない()
    {
        var baseDirectory = Path.Combine("C", "Apps", "Kado");

        Assert.Equal(
            baseDirectory,
            DataLocation.ExeDirectory(Path.Combine("C", "dotnet", "dotnet.exe"), baseDirectory));
    }

    [Fact]
    public void 従来の場所を使っている理由は画面に出せる言葉になる()
    {
        Assert.Null(DataLocation.DescribeFallback(LocalFallbackReason.None));
        Assert.Contains("exe のフォルダに書き込めないため、こちらを使っています",
            DataLocation.DescribeFallback(LocalFallbackReason.NotWritable));
        Assert.NotNull(DataLocation.DescribeFallback(LocalFallbackReason.MigrationFailed));
    }

    // ------------------------------------------------------------------
    // 実際に決める
    // ------------------------------------------------------------------

    [Fact]
    public void 新しく入れた人はexeのフォルダのdataを使う()
    {
        var result = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal(Beside, result.Directory);
        Assert.Equal(LocalFallbackReason.None, result.Reason);
        // 決めるだけで、作りはしない。作るのは実際に開くとき
        Assert.False(Directory.Exists(Beside));
        Assert.False(Directory.Exists(Local));
    }

    [Fact]
    public void 書き込めない場所なら従来の場所を使い続ける()
    {
        // 親が無いフォルダは、小さなファイルを作れない（root で動くテストでも権限に頼らず再現できる）
        var missingExe = Path.Combine(_root, "missing", "exe");
        MakeData(Local, "従来");

        var result = DataLocation.Resolve(missingExe, LocalAppData);

        Assert.Equal(Local, result.Directory);
        Assert.Equal(LocalFallbackReason.NotWritable, result.Reason);
        Assert.Contains(result.Notes, n => n.Contains("書き込めない"));
        Assert.Equal("従来", ReadTitle(Path.Combine(Local, "data.db")));
        Assert.False(File.Exists(Path.Combine(Local, DataMigration.MovedMarkerFileName)));
    }

    [Fact]
    public void 初回はKadoのデータをdataへ写し元は残して印を置く()
    {
        MakeData(Local, "予定A");
        File.WriteAllText(Path.Combine(Local, "google-tokens.dat"), "トークン");

        var result = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal(Beside, result.Directory);
        Assert.Equal(LocalFallbackReason.None, result.Reason);
        Assert.Equal("予定A", ReadTitle(Path.Combine(Beside, "data.db")));
        Assert.Equal("トークン", File.ReadAllText(Path.Combine(Beside, "google-tokens.dat")));

        // 元は消さない
        Assert.Equal("予定A", ReadTitle(Path.Combine(Local, "data.db")));
        Assert.Equal("トークン", File.ReadAllText(Path.Combine(Local, "google-tokens.dat")));
        Assert.True(File.Exists(Path.Combine(Local, DataMigration.MovedMarkerFileName)));
    }

    [Fact]
    public void 引っ越し済みなら二度目は写さずdataを使う()
    {
        MakeData(Local, "予定A");
        DataLocation.Resolve(Exe, LocalAppData);

        // 新しい場所で書き足したものが、元のデータで上書きされないこと
        using (var connection = Open(Path.Combine(Beside, "data.db")))
        {
            Execute(connection, "UPDATE t SET title = '新しい場所で変えた';");
        }

        var second = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal(Beside, second.Directory);
        Assert.Equal("新しい場所で変えた", ReadTitle(Path.Combine(Beside, "data.db")));
        Assert.Equal("予定A", ReadTitle(Path.Combine(Local, "data.db")));
    }

    [Fact]
    public void 旧い名前のフォルダからも直接dataへ写す()
    {
        MakeData(Legacy, "旧い名前の予定");

        var result = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal(Beside, result.Directory);
        Assert.Equal("旧い名前の予定", ReadTitle(Path.Combine(Beside, "data.db")));

        // 旧い名前のフォルダも消さない。Kado の名前へ付け替えもしない
        Assert.Equal("旧い名前の予定", ReadTitle(Path.Combine(Legacy, "data.db")));
        Assert.True(File.Exists(Path.Combine(Legacy, DataMigration.MovedMarkerFileName)));
        Assert.False(Directory.Exists(Local));
    }

    [Fact]
    public void 旧い名前と今の名前の両方にあれば今の名前のほうを写す()
    {
        MakeData(Legacy, "旧");
        MakeData(Local, "新");

        var result = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal("新", ReadTitle(Path.Combine(result.Directory, "data.db")));
        Assert.Equal("旧", ReadTitle(Path.Combine(Legacy, "data.db")));
    }

    [Fact]
    public void 引っ越しに失敗したら写しかけを片付けて従来の場所のまま使う()
    {
        Directory.CreateDirectory(Local);
        File.WriteAllText(Path.Combine(Local, "data.db"), "SQLite ではない中身");
        File.WriteAllText(Path.Combine(Local, "shell.log"), "ログ");

        var result = DataLocation.Resolve(Exe, LocalAppData);

        Assert.Equal(Local, result.Directory);
        Assert.Equal(LocalFallbackReason.MigrationFailed, result.Reason);
        Assert.Contains(result.Notes, n => n.Contains("失敗"));

        // 写しかけを残さない。次の起動でまた試せる
        Assert.False(Directory.Exists(Beside));
        Assert.False(Directory.Exists(Beside + DataMigration.StagingSuffix));

        // 元は無傷
        Assert.Equal("SQLite ではない中身", File.ReadAllText(Path.Combine(Local, "data.db")));
        Assert.Equal("ログ", File.ReadAllText(Path.Combine(Local, "shell.log")));
        Assert.False(File.Exists(Path.Combine(Local, DataMigration.MovedMarkerFileName)));
    }

    [Fact]
    public void 旧い名前のフォルダしか無く書けない場合は従来どおり名前を付け替える()
    {
        MakeData(Legacy, "旧");
        var missingExe = Path.Combine(_root, "missing", "exe");

        var result = DataLocation.Resolve(missingExe, LocalAppData);

        Assert.Equal(Local, result.Directory);
        Assert.Equal("旧", ReadTitle(Path.Combine(Local, "data.db")));
        Assert.False(Directory.Exists(Legacy));
    }

    // ------------------------------------------------------------------

    internal static void MakeData(string folder, string title)
    {
        Directory.CreateDirectory(folder);
        using var connection = Open(Path.Combine(folder, "data.db"));
        Execute(connection, "CREATE TABLE t (title TEXT);");
        Execute(connection, $"INSERT INTO t VALUES ('{title}');");
        Execute(connection, "PRAGMA user_version = 7;");
    }

    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string ReadTitle(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM t LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }
}
