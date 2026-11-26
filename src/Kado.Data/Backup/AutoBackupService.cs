using Microsoft.Data.Sqlite;

namespace Kado.Data.Backup;

/// <summary>
/// 起動時と、取り込み・復元・旧データ移行など元に戻せない操作の直前に取る、世代保存のバックアップ。
/// <para>
/// 設定画面から手で押すバックアップ（<see cref="DatabaseBackup.SaveTo"/>）とは別物。
/// 手のほうは利用者が選んだ1か所へ書き出すのに対し、こちらは
/// <c>%LocalAppData%\Kado\backups\</c> へ自動で積み、
/// 直近 <see cref="KeepCount"/> 本だけを残す（起動時の分と、操作の直前の分は別々に数える）。
/// </para>
/// <para>
/// 取り込み系は「Undo に積まない。戻したいときはバックアップから復元」という前提で
/// 作ってあるのに、そのバックアップが自動で取られていなかった。復元は再起動を伴う
/// 破壊的操作なので、直前に必ず1本残す。
/// </para>
/// </summary>
public static class AutoBackupService
{
    /// <summary>残す世代数。起動時の分と、取り込み・復元の直前の分を、別々にこの数ずつ残す。</summary>
    public const int KeepCount = 5;

    /// <summary>起動時の分のファイル名の接頭辞。</summary>
    private const string StartupPrefix = "data-";

    /// <summary>取り込み・復元の直前の分のファイル名の接頭辞。起動時の分の世代を押し出さないよう別にする。</summary>
    internal const string SafetyPrefix = "safety-";

    /// <summary>既定の書き出し先。</summary>
    public static string DefaultDirectory => Path.Combine(
        DataLocation.Directory, "backups");

    /// <summary>
    /// 起動時の分を取る。<b>1日1本</b>。その日の起動分がもうあれば取らない
    /// （1日に何度も起動しても、世代が同じ日の写しで埋まらない）。
    /// <para>
    /// 失敗しても例外を外に出さない。バックアップが取れないことより、それを理由に
    /// 起動そのものを止めてしまうことのほうが困る。
    /// </para>
    /// </summary>
    /// <param name="connection">書き出し元の接続。</param>
    /// <param name="directory">書き出し先。省略時は既定の場所。</param>
    /// <param name="now">名前に使う時刻。省略時は現在時刻。</param>
    /// <returns>その日の分が取れている（今回取った、またはもうあった）なら true。</returns>
    public static bool TryRunStartup(SqliteConnection connection, string? directory = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var dir = directory ?? DefaultDirectory;
            var at = now ?? DateTimeOffset.Now;

            if (HasBackupOfDay(dir, StartupPrefix, at)) return true;

            Write(connection, dir, StartupPrefix, at);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// 取り込み・復元など、元に戻せない操作の直前の分を取る。毎回取る。
    /// <para>起動時の分とは別の接頭辞で別に数えるので、操作を重ねても起動時の分の世代は減らない。</para>
    /// </summary>
    /// <param name="connection">書き出し元の接続。</param>
    /// <param name="directory">書き出し先。省略時は既定の場所。</param>
    /// <param name="now">名前に使う時刻。省略時は現在時刻。</param>
    /// <returns>書き出せたら true。</returns>
    public static bool TryRunSafety(SqliteConnection connection, string? directory = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            Write(connection, directory ?? DefaultDirectory, SafetyPrefix, now ?? DateTimeOffset.Now);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    private static void Write(SqliteConnection connection, string directory, string prefix, DateTimeOffset at)
    {
        DatabaseBackup.SaveTo(connection, Path.Combine(directory, $"{prefix}{at:yyyyMMdd-HHmmss}.db"));
        Prune(directory, prefix);
    }

    /// <summary>その日のうちに取った分（<paramref name="prefix"/> 付き）がもうあるか。</summary>
    private static bool HasBackupOfDay(string directory, string prefix, DateTimeOffset at)
    {
        if (!Directory.Exists(directory)) return false;

        return new DirectoryInfo(directory)
            .GetFiles($"{prefix}{at:yyyyMMdd}-*.db")
            .Length > 0;
    }

    /// <summary>
    /// 裏で起動時の分を取る（1日1本）。起動を待たせないための入口。
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
                TryRunStartup(connection, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                // 起動を止めない。取れなかったこと自体は次回の起動でまた試みる
            }
        });

    /// <summary>同じ接頭辞のうち直近 <see cref="KeepCount"/> 本だけ残し、古いものを消す。</summary>
    private static void Prune(string directory, string prefix)
    {
        var files = new DirectoryInfo(directory).GetFiles($"{prefix}*.db");

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
