using Dapper;
using Microsoft.Data.Sqlite;
using Kado.Data.Migrations;

namespace Kado.Data;

/// <summary>
/// データベースへの接続を作る。
/// <para>
/// 保存先は <c>%LOCALAPPDATA%\Kado\data.db</c>（要件書 3.2）。
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

    /// <summary>既定の保存先（<c>%LOCALAPPDATA%\Kado\data.db</c>）を開く。</summary>
    public static CalendarDatabase OpenDefault() => OpenFile(DefaultPath);

    /// <summary>保存先のフォルダ名。</summary>
    private const string FolderName = "Kado";

    /// <summary>
    /// 改名する前（SlideinaCalendar）の保存先のフォルダ名。
    /// <para>
    /// 予定・タスク・実働日・設定・バックアップ・Google のトークンは、すべて
    /// ここ1つのフォルダに入っている。名前だけ変えて置き去りにすると、更新した
    /// とたんに中身が空になったように見える。
    /// </para>
    /// </summary>
    private const string LegacyFolderName = "SlideinaCalendar";

    /// <summary>
    /// 既定の保存先のフォルダ。
    /// <para>
    /// 初めて求められたときに1回だけ決める。<see cref="ResolveDataDirectory"/> が
    /// 旧い名前からの引っ越しも兼ねているので、プロセスの途中で行き先が
    /// 変わらないようにしてある。
    /// </para>
    /// </summary>
    private static readonly Lazy<string> DataDirectory = new(() => ResolveDataDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    /// <summary>既定の保存先。</summary>
    public static string DefaultPath => System.IO.Path.Combine(DataDirectory.Value, "data.db");

    /// <summary>
    /// 保存先のフォルダを決める。旧い名前のフォルダしか無ければ、新しい名前へ移す。
    /// <para>
    /// 同じドライブの中なので、移すといっても名前を付け替えるだけで中身は動かない。
    /// <b>移せなかったときは旧い場所をそのまま使う。</b>アプリの名前が変わったという
    /// 都合で、書いてきたものを見失わせるわけにはいかない。
    /// </para>
    /// </summary>
    /// <param name="root">%LOCALAPPDATA% にあたるフォルダ。</param>
    internal static string ResolveDataDirectory(string root)
    {
        var target = System.IO.Path.Combine(root, FolderName);
        var legacy = System.IO.Path.Combine(root, LegacyFolderName);

        // 新しい側がもう在るなら引っ越しは済んでいる。旧い側が無いなら
        // 引っ越すものが無い（新しく入れた人）
        if (Directory.Exists(target) || !Directory.Exists(legacy)) return target;

        try
        {
            Directory.Move(legacy, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }

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
            // 共有キャッシュ（Cache=Shared）は外してある。WAL + busy_timeout だけで
            // UI 用・Google 同期用の2本の接続が同時に読み書きできることは
            // DualConnectionTests で確かめてある。共有キャッシュはテーブル単位の
            // ロックに変わるぶん、書き込みどうしがかえってぶつかりやすくなる
            // （SQLite 自身が既定では勧めていない）
        };

        return new CalendarDatabase(path, builder.ToString());
    }

    /// <summary>
    /// メモリ上のデータベースを開く。テスト用。
    /// <para>
    /// 中身は開いた接続の間だけ生きる（<c>:memory:</c> と同じ）。共有キャッシュ
    /// （Cache=Shared）は付けていない。付けるのは「同じ名前で開いた<b>別の</b>接続からも
    /// 見える」ようにするためだが、このアプリのテストは1つの
    /// <see cref="CalendarDatabase"/> インスタンスにつき <see cref="Connect"/> を
    /// 1回しか呼ばないので、要らない。接続を閉じると消える。
    /// </para>
    /// </summary>
    public static CalendarDatabase OpenInMemory(string? name = null)
    {
        var source = name ?? $"kado-{Guid.NewGuid():N}";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.Memory,
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

        // WAL でも書き込みは同時に1つしか通らない。UI 用と Google 同期用で接続を
        // 分けているので、双方が同時に書こうとすると SQLITE_BUSY になりうる。
        // 待たせて順番に通すことで、即座に例外にしない
        connection.Execute("PRAGMA busy_timeout = 5000;");

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
