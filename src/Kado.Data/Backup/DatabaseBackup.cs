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
    public static void SaveTo(SqliteConnection connection, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 古い内容が残っていると、書き出し先のページ数が元より多い場合に差分が残る
        if (File.Exists(full)) File.Delete(full);

        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using (var destination = new SqliteConnection(destinationConnectionString))
        {
            destination.Open();
            connection.BackupDatabase(destination);
        }

        // 書き出し先をプールに残すと、このあとファイルを開いたり消したりするときに掴まれたままになる
        SqliteConnection.ClearPool(new SqliteConnection(destinationConnectionString));

        // 黙って失敗していないか確かめる。バックアップは取れたつもりで中身が無いのが一番困る
        if (!File.Exists(full) || new FileInfo(full).Length == 0)
        {
            throw new IOException($"バックアップを書き出せませんでした: {full}");
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

        var target = Path.GetFullPath(databasePath);
        string? rescued = null;

        if (File.Exists(target))
        {
            rescued = target + ".bak";
            File.Copy(target, rescued, overwrite: true);
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.Copy(backupPath, target, overwrite: true);

        // 古い WAL が残っていると、差し替えた本体と食い違う
        foreach (var suffix in (string[])["-wal", "-shm"])
        {
            var side = target + suffix;
            if (File.Exists(side)) File.Delete(side);
        }

        return rescued;
    }
}
