using Dapper;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Data.Migrations;

namespace SlideinaCalendar.Data;

/// <summary>
/// データベースへの接続を作る。
/// <para>
/// 保存先は <c>%LOCALAPPDATA%\SlideinaCalendar\data.db</c>（要件書 3.2）。
/// </para>
/// </summary>
public sealed class CalendarDatabase
{
    private readonly string _connectionString;

    /// <summary>データベースファイルのパス。メモリ上のものなら <c>:memory:</c>。</summary>
    public string Path { get; }

    private CalendarDatabase(string path, string connectionString)
    {
        Path = path;
        _connectionString = connectionString;
    }

    /// <summary>既定の保存先（<c>%LOCALAPPDATA%\SlideinaCalendar\data.db</c>）を開く。</summary>
    public static CalendarDatabase OpenDefault() => OpenFile(DefaultPath);

    /// <summary>既定の保存先。</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SlideinaCalendar",
        "data.db");

    /// <summary>ファイルを開く。親ディレクトリが無ければ作る。</summary>
    public static CalendarDatabase OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 既定の共有なしだと、同じファイルを開いた接続どうしで即座にロック競合する
            Cache = SqliteCacheMode.Shared,
        };

        return new CalendarDatabase(path, builder.ToString());
    }

    /// <summary>
    /// メモリ上のデータベースを開く。テスト用。
    /// <para>
    /// 名前を付けて共有モードにするので、同じ名前で開いた接続からは同じ内容が見える。
    /// 接続をすべて閉じると消える。
    /// </para>
    /// </summary>
    public static CalendarDatabase OpenInMemory(string? name = null)
    {
        var source = name ?? $"slideina-{Guid.NewGuid():N}";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        };

        return new CalendarDatabase(":memory:", builder.ToString());
    }

    /// <summary>
    /// 接続を開く。呼び出し側が破棄すること。
    /// <para>外部キー制約は既定で無効なので、接続ごとに有効化する。</para>
    /// </summary>
    public SqliteConnection Connect()
    {
        SqliteTypeHandlers.Register();

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        connection.Execute("PRAGMA foreign_keys = ON;");
        // クラッシュ時の復旧力と書き込み性能のバランスを取る。常駐アプリなので
        // 書き込みは細かく頻繁に起きる
        connection.Execute("PRAGMA journal_mode = WAL;");
        connection.Execute("PRAGMA synchronous = NORMAL;");

        return connection;
    }

    /// <summary>接続を開き、スキーマを最新まで進める。</summary>
    public SqliteConnection ConnectAndMigrate()
    {
        var connection = Connect();
        try
        {
            DatabaseMigrator.Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
