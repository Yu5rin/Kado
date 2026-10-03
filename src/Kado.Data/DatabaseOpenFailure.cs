using Microsoft.Data.Sqlite;

namespace Kado.Data;

/// <summary>データベースを開けなかった理由の種類。起動時に何を案内するかを決める。</summary>
public enum DatabaseOpenFailureKind
{
    /// <summary>ほかのプロセス（や別の Kado）が使っている。一時的なことが多い。<b>壊れているのではない。</b></summary>
    InUse,

    /// <summary>ファイルが壊れている、あるいは SQLite のファイルではない。</summary>
    Corrupt,

    /// <summary>新しい版のアプリで作ったデータ。古い版では開けない。</summary>
    NewerVersion,

    /// <summary>保存先へ書き込めない・開けない（権限・読み取り専用・I/O の失敗）。</summary>
    NoAccess,

    /// <summary>ディスクがいっぱい。</summary>
    DiskFull,

    /// <summary>上のどれでもない。</summary>
    Other,
}

/// <summary>
/// 起動時にデータベースを開けなかったときの見分けと文言。
/// <para>
/// <b>一時的なロックを「壊れている」扱いにして、健全なデータベースを退避させてはいけない。</b>
/// 「壊れたものをどけて新しく始める」を勧めるのは、本当に壊れているとき
/// （SQLITE_CORRUPT・NOTADB）と、新しい版のデータだったときだけにする。
/// </para>
/// </summary>
public static class DatabaseOpenFailure
{
    // Windows の「共有違反」「ロック違反」。ほかのプロセスがファイルを掴んでいる
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>例外から種類を決める。</summary>
    public static DatabaseOpenFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SqliteException sqlite => ClassifySqlite(sqlite),

            // 新しい版で作ったデータ（DatabaseMigrator が投げる）
            InvalidOperationException => DatabaseOpenFailureKind.NewerVersion,

            UnauthorizedAccessException => DatabaseOpenFailureKind.NoAccess,

            IOException io when io.HResult is SharingViolation or LockViolation => DatabaseOpenFailureKind.InUse,
            IOException => DatabaseOpenFailureKind.NoAccess,

            _ => DatabaseOpenFailureKind.Other,
        };
    }

    private static DatabaseOpenFailureKind ClassifySqlite(SqliteException exception) =>
        SqliteFailure.Classify(exception) switch
        {
            SqliteFailureKind.Busy => DatabaseOpenFailureKind.InUse,
            SqliteFailureKind.Corrupt => DatabaseOpenFailureKind.Corrupt,
            SqliteFailureKind.DiskFull => DatabaseOpenFailureKind.DiskFull,
            SqliteFailureKind.ReadOnly or SqliteFailureKind.Io => DatabaseOpenFailureKind.NoAccess,
            _ => DatabaseOpenFailureKind.Other,
        };

    /// <summary>
    /// 「壊れたデータをどけて新しく始める」「バックアップから戻す」を選ばせてよいか。
    /// <para>ロック・権限・ディスクの空き・想定外の失敗では勧めない（データは無事なことが多い）。</para>
    /// </summary>
    public static bool OffersSetAside(DatabaseOpenFailureKind kind) =>
        kind is DatabaseOpenFailureKind.Corrupt or DatabaseOpenFailureKind.NewerVersion;

    /// <summary>
    /// 時間をおいてやり直せば開く見込みがあるか。
    /// <para>ほかのプロセスが使っているだけなら、待てば開く。権限やディスクの空きは利用者が直せば開く。</para>
    /// </summary>
    public static bool OffersRetry(DatabaseOpenFailureKind kind) => !OffersSetAside(kind);

    /// <summary>利用者に出す本文（先頭の説明）。例外の型名は含まない。</summary>
    public static string Describe(DatabaseOpenFailureKind kind, string databasePath) => kind switch
    {
        DatabaseOpenFailureKind.InUse =>
            "データを使っているほかのアプリ（もう一つの Kado など）があり、開けませんでした。"
            + "データは壊れていません。\n\n少し待ってからやり直すか、Kado がほかに動いていないか確かめてください。",

        DatabaseOpenFailureKind.NoAccess =>
            "データの保存先を開けませんでした。保存先に書き込む権限がない、またはほかのアプリ"
            + "（ウイルス対策ソフトなど）が使っている可能性があります。データは壊れていません。"
            + $"\n\n保存先: {databasePath}\n\nフォルダの権限を確かめるか、管理者にご確認ください。",

        DatabaseOpenFailureKind.DiskFull =>
            $"ディスクの空きが足りず、データを開けませんでした。データは壊れていません。\n\n保存先: {databasePath}\n\n"
            + "空き容量を作ってから、やり直してください。",

        DatabaseOpenFailureKind.NewerVersion =>
            "データを開けませんでした。新しい版の Kado で作ったデータを、古い版で開こうとした可能性があります。"
            + "\n\nまず Kado を新しい版に更新してください。",

        DatabaseOpenFailureKind.Corrupt =>
            "データを開けませんでした。ファイルが壊れている可能性があります。",

        _ => "データを開けませんでした。原因を特定できませんでした。データは壊れていない可能性があります。",
    };
}
