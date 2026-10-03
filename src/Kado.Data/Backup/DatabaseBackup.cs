using Microsoft.Data.Sqlite;

namespace Kado.Data.Backup;

/// <summary>
/// データベースのバックアップと復元。
/// <para>
/// 書き出しには SQLite のオンラインバックアップ API（<c>BackupDatabase</c>）を使う。
/// ファイルを直接コピーすると、書き込みの途中や WAL に未反映の内容がある状態では
/// 壊れた写しになるため、コピーは使わない。
/// </para>
/// <para>
/// <b><c>VACUUM INTO</c> は使わない。</b>メモリ上のデータベースに対して実行すると、
/// 例外も返り値のエラーも出さずに<b>何も書き出さない</b>。黙って失敗されると
/// バックアップを取ったつもりで中身が無い、という最悪の事態になる。
/// オンラインバックアップ API ならメモリ上のものからも確実に書き出せる。
/// </para>
/// </summary>
public static class DatabaseBackup
{
    /// <summary>
    /// 現在の内容を別ファイルへ書き出す。
    /// </summary>
    /// <param name="connection">書き出し元の接続。</param>
    /// <param name="destinationPath">書き出し先。既にあれば上書きする。</param>
    /// <exception cref="SqliteException">書き込めない場所・ディスクいっぱいなど。呼び出し側が受けて文言にする。</exception>
    /// <exception cref="IOException">ファイルの操作に失敗した。</exception>
    /// <exception cref="UnauthorizedAccessException">書き込む権限がない。</exception>
    public static void SaveTo(SqliteConnection connection, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 一時ファイルへ書いてから置き換える。書き出し先へ直接書くと、途中で転んだとき
        // （書き込めない場所・ディスクいっぱい）に、すでにあった良いバックアップまで失う
        var temporary = full + ".writing";
        DeleteIfExists(temporary);

        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = temporary,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        try
        {
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                destination.Open();
                connection.BackupDatabase(destination);
            }

            // 書き出し先をプールに残すと、このあとファイルを開いたり消したりするときに掴まれたままになる
            SqliteConnection.ClearPool(new SqliteConnection(destinationConnectionString));

            // 黙って失敗していないか確かめる。バックアップは取れたつもりで中身が無いのが一番困る
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            {
                throw new IOException($"バックアップを書き出せませんでした: {full}");
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            // 書きかけを残さない
            SqliteConnection.ClearPool(new SqliteConnection(destinationConnectionString));
            DeleteIfExists(temporary);
            DeleteIfExists(temporary + "-wal");
            DeleteIfExists(temporary + "-shm");
            DeleteIfExists(temporary + "-journal");
            throw;
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても、本来の失敗の理由のほうを優先して伝える
        }
    }

    /// <summary>
    /// タイムスタンプ付きの名前で書き出す。
    /// </summary>
    /// <param name="connection">書き出し元の接続。</param>
    /// <param name="directory">書き出し先のディレクトリ。</param>
    /// <param name="now">名前に使う時刻。省略時は現在時刻。</param>
    /// <returns>書き出したファイルのパス。</returns>
    public static string SaveToDirectory(
        SqliteConnection connection, string directory, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var stamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(directory, $"data-{stamp}.db");

        SaveTo(connection, path);
        return path;
    }

    /// <summary>
    /// 書き出したファイルで置き換える。
    /// <para>
    /// 復元は<b>接続を閉じてから</b>行う。開いたまま差し替えると、WAL が食い違って壊れる。
    /// 置き換え前の内容は <c>.bak</c> として残すので、復元に失敗しても戻せる。
    /// </para>
    /// </summary>
    /// <param name="backupPath">復元元。</param>
    /// <param name="databasePath">復元先。</param>
    /// <returns>退避した元ファイルのパス。元が無ければ null。</returns>
    /// <exception cref="SqliteException">復元元が SQLite のデータベースとして読めない。このとき、いまのデータには触れていない。</exception>
    public static string? RestoreFrom(string backupPath, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // 接続は使い終わってもプールに残り、ファイルを掴んだままになる。
        // WAL の内容が本体に反映されるのもプールから外れたときなので、
        // 差し替える前に必ず解放する
        SqliteConnection.ClearAllPools();

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException($"復元元が見つかりません: {backupPath}", backupPath);
        }

        // 何も動かす前に、選ばれたファイルが読めるデータベースかを確かめる。
        // 違うファイルを選んでも、いまのデータには触れずに断る
        EnsureReadableDatabase(backupPath);

        var target = Path.GetFullPath(databasePath);
        string? rescued = null;

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 大きいコピー（ディスクいっぱいや権限で転びやすい）は、本体に触れる前に別名で済ませる。
        // 本体へ直接コピーすると、途中で転んだとき本体が半端なまま残る
        var staging = target + ".restoring";
        DeleteIfExists(staging);

        try
        {
            File.Copy(backupPath, staging, overwrite: true);

            if (File.Exists(target))
            {
                rescued = target + ".bak";
                File.Copy(target, rescued, overwrite: true);
            }

            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            DeleteIfExists(staging);
            throw;
        }

        // 古い WAL が残っていると、差し替えた本体と食い違う
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            var side = target + suffix;
            if (File.Exists(side)) File.Delete(side);
        }

        return rescued;
    }

    /// <summary>
    /// ファイルが、読める SQLite のデータベースか確かめる（読み取り専用で開く）。
    /// <para>
    /// SQLite のファイルでなければ <see cref="SqliteException"/>（NOTADB）、
    /// 壊れていれば <see cref="IOException"/> を投げる。復元の前に呼んで、
    /// 違うファイルを選んだときにいまのデータへ触れないようにする。
    /// </para>
    /// </summary>
    public static void EnsureReadableDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 先頭16バイトの印。SQLite のファイルでなければ、開く前にここで断る
        var head = new byte[SqliteHeader.Length];
        int read;

        using (var stream = File.OpenRead(path))
        {
            read = stream.Read(head, 0, head.Length);
        }

        if (read < head.Length || !head.AsSpan().SequenceEqual(SqliteHeader))
        {
            throw new SqliteException("file is not a database", NotADatabaseCode);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";

            if (command.ExecuteScalar() is not string result
                || !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"バックアップのファイルが壊れています: {path}");
            }
        }
        catch (SqliteException ex) when (SqliteFailure.Classify(ex)
                                         is SqliteFailureKind.Io or SqliteFailureKind.ReadOnly
                                         or SqliteFailureKind.Busy)
        {
            // 読み取り専用の置き場など、中身の検査のために開けないだけ。印は確かめたので進める
        }
    }

    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>SQLITE_NOTADB。</summary>
    private const int NotADatabaseCode = 26;
}
