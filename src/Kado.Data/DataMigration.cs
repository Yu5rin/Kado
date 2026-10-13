using Microsoft.Data.Sqlite;

namespace Kado.Data;

/// <summary>引っ越し（写し）の結果。</summary>
/// <param name="Succeeded">写して確かめるまで済んだか。</param>
/// <param name="Message">shell.log に残す言葉。</param>
public sealed record DataMigrationResult(bool Succeeded, string Message);

/// <summary>
/// 従来の保存先のデータを、exe と同じフォルダの <c>data</c> へ写す。
/// <para>
/// <b>写して、確かめて、元は消さずに残す。</b>利用者のデータを失わせないことが最優先。
/// </para>
/// <list type="number">
/// <item><description>まず <c>data.migrating</c> という別の名前のフォルダへ写す（途中で終わっても <c>data</c> には現れない）</description></item>
/// <item><description>data.db は SQLite のバックアップ API で写す（-wal / -shm を取りこぼさないため）。写したあと開いて <c>integrity_check</c> が <c>ok</c> か確かめる</description></item>
/// <item><description>それ以外（設定・トークン・ログ・バックアップなど）はそのまま写す。<c>updates</c>（入れ替え待ち）は写さない</description></item>
/// <item><description>確かめが通ったら <c>data</c> の名前にし、元のフォルダに <c>移しました.txt</c> を置く。元は消さない</description></item>
/// </list>
/// 失敗したら写しかけを片付けて失敗を返す。呼んだ側は今回は元の場所のまま起動する。
/// </summary>
public static class DataMigration
{
    /// <summary>元のフォルダに置く、移したことを知らせるファイルの名前。</summary>
    public const string MovedMarkerFileName = "移しました.txt";

    /// <summary>写している途中のフォルダにつける接尾辞。</summary>
    internal const string StagingSuffix = ".migrating";

    /// <summary>写さないもの（最上位の名前）。</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        // 自動更新の入れ替え待ち。新しい場所で作り直す
        "updates",
        // データベースは別の方法で写す
        "data.db", "data.db-wal", "data.db-shm", "data.db-journal",
        MovedMarkerFileName,
    };

    /// <summary>
    /// 写す。
    /// </summary>
    /// <param name="source">元のフォルダ。</param>
    /// <param name="target">写し先（<c>data</c>）。</param>
    /// <param name="now">移した時刻（<c>移しました.txt</c> に書く）。</param>
    public static DataMigrationResult Copy(string source, string target, DateTimeOffset now)
    {
        var staging = target + StagingSuffix;

        try
        {
            // 前に途中で終わった写しかけが残っていたら、捨ててやり直す
            DeleteDirectory(staging);
            Directory.CreateDirectory(staging);

            CopyEntries(source, staging);

            var sourceDb = Path.Combine(source, DataLocation.DatabaseFileName);
            var stagedDb = Path.Combine(staging, DataLocation.DatabaseFileName);

            CopyDatabase(sourceDb, stagedDb);
            VerifyDatabase(sourceDb, stagedDb);

            Place(staging, target);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 写しかけを片付ける。元には一切触れていない
            DeleteDirectory(staging);

            return new DataMigrationResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // 元は残す。印を置けなくても、引っ越し自体は済んでいる
        try
        {
            WriteMovedMarker(source, target, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DataMigrationResult(true, $"{MovedMarkerFileName} は置けませんでした（{ex.Message}）");
        }

        return new DataMigrationResult(true, "整合性の確認: ok");
    }

    /// <summary>元のフォルダに置く、移したことを知らせる文面。</summary>
    internal static string BuildMovedMarker(string target, DateTimeOffset now) =>
        $"このフォルダの中身は、{now:yyyy-MM-dd HH:mm} に次の場所へ移しました。{Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"    {target}{Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"Kado はこれからそちらを使います。このフォルダは念のため消さずに残してあります。{Environment.NewLine}" +
        $"新しい場所で問題なく使えていることを確かめてから、削除して構いません。{Environment.NewLine}";

    private static void WriteMovedMarker(string source, string target, DateTimeOffset now) =>
        File.WriteAllText(Path.Combine(source, MovedMarkerFileName), BuildMovedMarker(target, now));

    private static void CopyEntries(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (Skipped.Contains(name)) continue;

            var to = Path.Combine(destination, name);

            if (Directory.Exists(entry)) CopyDirectory(entry, to);
            else File.Copy(entry, to, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var to = Path.Combine(destination, Path.GetFileName(entry));

            if (Directory.Exists(entry)) CopyDirectory(entry, to);
            else File.Copy(entry, to, overwrite: true);
        }
    }

    /// <summary>SQLite のバックアップ API で写す。-wal に残っている分も含めて一貫した形になる。</summary>
    private static void CopyDatabase(string sourceDb, string destinationDb)
    {
        if (!File.Exists(sourceDb)) throw new FileNotFoundException("元のデータベースがありません", sourceDb);

        // プールに残すと、このあとフォルダの名前を変えるときに掴まれたままになる
        using var source = new SqliteConnection(ConnectionString(sourceDb, SqliteOpenMode.ReadOnly));
        using var destination = new SqliteConnection(ConnectionString(destinationDb, SqliteOpenMode.ReadWriteCreate));

        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>写したものを開き直して、壊れていないこと・元と同じ版であることを確かめる。</summary>
    private static void VerifyDatabase(string sourceDb, string copiedDb)
    {
        using var copied = new SqliteConnection(ConnectionString(copiedDb, SqliteOpenMode.ReadOnly));
        copied.Open();

        var check = Scalar(copied, "PRAGMA integrity_check;") as string;
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"写したデータベースの整合性の確認が通りませんでした（{check}）");
        }

        using var original = new SqliteConnection(ConnectionString(sourceDb, SqliteOpenMode.ReadOnly));
        original.Open();

        // スキーマの版と、表・索引の数が元と合うか
        if (!Equals(Scalar(original, "PRAGMA user_version;"), Scalar(copied, "PRAGMA user_version;")) ||
            !Equals(Scalar(original, "SELECT COUNT(*) FROM sqlite_master;"),
                    Scalar(copied, "SELECT COUNT(*) FROM sqlite_master;")))
        {
            throw new IOException("写したデータベースの中身が元と合いませんでした");
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();

    /// <summary>写し終わったフォルダを <c>data</c> の名前にする。</summary>
    private static void Place(string staging, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(staging, target);
            return;
        }

        // data だけ先にあった（ログだけ、など。データベースは無い）。中身を消さずに重ねる。
        // data.db は最後に動かす（途中で転んだとき、データベースだけあって他が足りない形にしない）
        MoveInto(staging, target, last: DataLocation.DatabaseFileName);
        Directory.Delete(staging, recursive: true);
    }

    private static void MoveInto(string from, string to, string? last = null)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(from)
                     .OrderBy(e => string.Equals(Path.GetFileName(e), last, StringComparison.OrdinalIgnoreCase)))
        {
            var destination = Path.Combine(to, Path.GetFileName(entry));

            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(destination);
                MoveInto(entry, destination);
            }
            else
            {
                File.Move(entry, destination, overwrite: true);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 片付けで転んでも、次の起動でまた消す
        }
    }
}
