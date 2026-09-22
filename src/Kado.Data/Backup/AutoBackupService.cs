using Microsoft.Data.Sqlite;

namespace Kado.Data.Backup;

/// <summary>
/// 起動時と、取り込み・復元・旧データ移行など元に戻せない操作の直前に取る、世代保存のバックアップ。
/// <para>
/// 設定画面から手で押すバックアップ（<see cref="DatabaseBackup.SaveTo"/>）とは別物。
/// 手のほうは利用者が選んだ1か所へ書き出すのに対し、こちらは
/// <c>%LocalAppData%\Kado\backups\</c> へ自動で積み、
/// 直近 <see cref="KeepCount"/> 本だけを残す。
/// </para>
/// <para>
/// 取り込み系は「Undo に積まない。戻したいときはバックアップから復元」という前提で
/// 作ってあるのに、そのバックアップが自動で取られていなかった。復元は再起動を伴う
/// 破壊的操作なので、直前に必ず1本残す。
/// </para>
/// </summary>
public static class AutoBackupService
{
    /// <summary>残す世代数。これを超えたぶんは古いものから消す。</summary>
    public const int KeepCount = 5;

    /// <summary>書き出したファイルの名前に使う接頭辞（<see cref="DatabaseBackup.SaveToDirectory"/> と共通）。</summary>
    private const string FilePattern = "data-*.db";

    /// <summary>既定の書き出し先。</summary>
    public static string DefaultDirectory => Path.Combine(
        Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "backups");

    /// <summary>
    /// 1本書き出し、直近 <see cref="KeepCount"/> 本だけを残す。
    /// <para>
    /// 失敗しても例外を外に出さない。バックアップが取れないことより、それを理由に
    /// 起動や取り込み・復元そのものを止めてしまうことのほうが困る。うまくいったかは
    /// 戻り値で分かる。
    /// </para>
    /// </summary>
    /// <param name="connection">書き出し元の接続。</param>
    /// <param name="directory">書き出し先。省略時は既定の場所。</param>
    /// <param name="now">名前に使う時刻。省略時は現在時刻。</param>
    /// <returns>書き出せたら true。</returns>
    public static bool TryRun(SqliteConnection connection, string? directory = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var dir = directory ?? DefaultDirectory;
            DatabaseBackup.SaveToDirectory(connection, dir, now);
            Prune(dir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// 裏で1本取る。起動を待たせないための入口。
    /// <para>
    /// 起動処理を進めている接続とは別の接続を自前で開いて使う。同じ接続を別スレッドから
    /// 同時に叩くと、片方が張っているトランザクションともう片方の操作がかち合って
    /// 例外になることがあるため（<c>App.xaml.cs</c> が同期用に接続を分けているのと同じ理由）。
    /// </para>
    /// </summary>
    /// <param name="databasePath">バックアップ元のデータベースファイル。既定は通常の保存先。</param>
    /// <param name="directory">書き出し先。省略時は既定の場所。</param>
    public static Task RunInBackgroundAsync(string? databasePath = null, string? directory = null) =>
        Task.Run(() =>
        {
            try
            {
                using var connection = CalendarDatabase.OpenFile(databasePath ?? CalendarDatabase.DefaultPath).Connect();
                TryRun(connection, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                // 起動を止めない。取れなかったこと自体は次回の起動でまた試みる
            }
        });

    /// <summary>直近 <see cref="KeepCount"/> 本だけ残し、古いものを消す。</summary>
    private static void Prune(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles(FilePattern);

        // ファイル名がタイムスタンプ（yyyyMMdd-HHmmss）を含む形なので、文字列順に
        // 並べればそのまま新しい順になる
        var stale = files
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(KeepCount);

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 1本消せなくても残りは諦めない
            }
        }
    }
}
