using System.Security;

namespace Kado.Data;

/// <summary>データの置き場所をどう決めたか（<see cref="DataLocation.Decide"/> の答え）。</summary>
public enum DataPlacement
{
    /// <summary>exe と同じフォルダの <c>data</c> をそのまま使う（初めて入れた人、または引っ越し済み）。</summary>
    UseBesideExe,

    /// <summary><c>%LOCALAPPDATA%\Kado</c> のデータを <c>data</c> へ写してから使う（初回だけ）。</summary>
    MigrateFromLocalAppData,

    /// <summary>旧い名前（<c>%LOCALAPPDATA%\SlideinaCalendar</c>）のデータを <c>data</c> へ写してから使う。</summary>
    MigrateFromLegacy,

    /// <summary>従来の <c>%LOCALAPPDATA%\Kado</c> を使い続ける。</summary>
    UseLocalAppData,
}

/// <summary>従来の場所（<c>%LOCALAPPDATA%\Kado</c>）を使っている理由。</summary>
public enum LocalFallbackReason
{
    /// <summary>使っていない（exe のフォルダの <c>data</c> を使っている）。</summary>
    None,

    /// <summary>exe のフォルダに書き込めない（Program Files・読み取り専用の共有フォルダなど）。</summary>
    NotWritable,

    /// <summary>引っ越しに失敗した。次の起動でもう一度試す。</summary>
    MigrationFailed,
}

/// <summary>保存先の決定の結果。</summary>
/// <param name="Directory">使うフォルダ。</param>
/// <param name="Reason">従来の場所を使っているときの理由。</param>
/// <param name="Notes">shell.log に残す行（この時点ではまだログを開けないので溜めておく）。</param>
public sealed record DataLocationResult(string Directory, LocalFallbackReason Reason, IReadOnlyList<string> Notes);

/// <summary>
/// データの保存先を決める、ただ1つの入口。
/// <para>
/// 既定は「exe と同じフォルダの <c>data</c>」。PC を引っ越すとき、フォルダごとコピーすれば
/// 持っていける。データベース・設定・Google のトークン・ログ・バックアップ・自動更新の
/// 入れ替え待ちは、すべてここを通る。<b>ここ以外で <c>LocalApplicationData</c> を直に使わない</b>
/// （<c>DataLocationTests</c> が見張っている）。
/// </para>
/// <para>
/// exe のフォルダに書き込めないときは、従来の <c>%LOCALAPPDATA%\Kado</c> を使い続ける。
/// 初めて <c>data</c> を使うときだけ、従来の場所から写す（<see cref="DataMigration"/>）。
/// 元の場所は消さずに残す。
/// </para>
/// </summary>
public static class DataLocation
{
    /// <summary>exe と同じフォルダに作る、データのフォルダ名。</summary>
    public const string BesideExeFolderName = "data";

    /// <summary>データベースのファイル名。このファイルがあれば「データがある」と見なす。</summary>
    public const string DatabaseFileName = "data.db";

    private static readonly Lazy<DataLocationResult> Current = new(() =>
        Resolve(
            ExeDirectory(Environment.ProcessPath, AppContext.BaseDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    /// <summary>
    /// 保存先を決める（初回だけ引っ越しも行う）。
    /// <para>データベースや設定、ログを開く前に、起動の最初で呼ぶこと。2回目以降は決めた結果を返すだけ。</para>
    /// </summary>
    public static DataLocationResult EnsureResolved() => Current.Value;

    /// <summary>データの保存先のフォルダ。</summary>
    public static string Directory => Current.Value.Directory;

    /// <summary>データベースのパス。</summary>
    public static string DatabasePath => System.IO.Path.Combine(Directory, DatabaseFileName);

    /// <summary>保存先の中のファイル・フォルダのパス。</summary>
    public static string PathOf(string name) => System.IO.Path.Combine(Directory, name);

    /// <summary>従来の <c>%LOCALAPPDATA%\Kado</c> を使っているか。</summary>
    public static bool IsLocalAppDataFallback => Current.Value.Reason != LocalFallbackReason.None;

    /// <summary>shell.log に残す、保存先を決めたときの行。</summary>
    public static IReadOnlyList<string> Notes => Current.Value.Notes;

    /// <summary>設定画面に出す、従来の場所を使っている理由。使っていなければ null。</summary>
    public static string? FallbackNote => DescribeFallback(Current.Value.Reason);

    /// <summary>従来の場所を使っている理由を、画面に出す言葉にする。</summary>
    public static string? DescribeFallback(LocalFallbackReason reason) => reason switch
    {
        LocalFallbackReason.NotWritable => "exe のフォルダに書き込めないため、こちらを使っています",
        LocalFallbackReason.MigrationFailed => "exe のフォルダの data へ移せなかったため、今回はこちらを使っています（次の起動でもう一度試します）",
        _ => null,
    };

    // ------------------------------------------------------------------
    // 判断（純粋な関数）
    // ------------------------------------------------------------------

    /// <summary>
    /// どこを使うか。ファイルには触れず、引数だけで決める。
    /// </summary>
    /// <param name="writable">exe のフォルダの <c>data</c> に書き込めるか。</param>
    /// <param name="besideHasData"><c>data</c> にもうデータ（data.db）があるか。</param>
    /// <param name="localHasData"><c>%LOCALAPPDATA%\Kado</c> にデータがあるか。</param>
    /// <param name="legacyHasData"><c>%LOCALAPPDATA%\SlideinaCalendar</c> にデータがあるか。</param>
    public static DataPlacement Decide(bool writable, bool besideHasData, bool localHasData, bool legacyHasData)
    {
        // 書けないなら、従来どおり。書けなければ写せもしない
        if (!writable) return DataPlacement.UseLocalAppData;

        // data にもうある（引っ越し済み、フォルダごと持ってきた、初めて入れた人の2回目以降）。
        // 古い場所にデータが残っていても、新しいほうを使う
        if (besideHasData) return DataPlacement.UseBesideExe;

        if (localHasData) return DataPlacement.MigrateFromLocalAppData;
        if (legacyHasData) return DataPlacement.MigrateFromLegacy;

        // 何も無い（新しく入れた人）
        return DataPlacement.UseBesideExe;
    }

    /// <summary>
    /// exe があるフォルダ。単一ファイル発行でも正しく取れるよう、<c>Assembly.Location</c> は使わない
    /// （単一ファイルでは空になる）。
    /// </summary>
    /// <param name="processPath"><c>Environment.ProcessPath</c>。</param>
    /// <param name="baseDirectory"><c>AppContext.BaseDirectory</c>。</param>
    public static string ExeDirectory(string? processPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            // dotnet Kado.dll で動かしているときは、dotnet 本体のフォルダを指してしまう
            var name = System.IO.Path.GetFileNameWithoutExtension(processPath);
            var folder = System.IO.Path.GetDirectoryName(processPath);

            if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(folder))
            {
                return folder;
            }
        }

        return baseDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    // ------------------------------------------------------------------
    // 実際に決める（フォルダを見る・写す）
    // ------------------------------------------------------------------

    /// <summary>
    /// 保存先を決める。テストから一時フォルダで試せるよう、場所を引数で受ける。
    /// </summary>
    /// <param name="exeDirectory">exe のフォルダ。</param>
    /// <param name="localAppData">%LOCALAPPDATA% にあたるフォルダ。</param>
    internal static DataLocationResult Resolve(string exeDirectory, string localAppData)
    {
        var target = System.IO.Path.Combine(exeDirectory, BesideExeFolderName);
        var local = System.IO.Path.Combine(localAppData, CalendarDatabase.FolderName);
        var legacy = System.IO.Path.Combine(localAppData, CalendarDatabase.LegacyFolderName);
        var notes = new List<string>();

        var writable = CanWrite(target, out var writeError);
        var placement = Decide(writable, HasData(target), HasData(local), HasData(legacy));

        switch (placement)
        {
            case DataPlacement.UseBesideExe:
                return new DataLocationResult(target, LocalFallbackReason.None, notes);

            case DataPlacement.UseLocalAppData:
            {
                var directory = ResolveLocalAppData(localAppData);
                notes.Add($"保存先: exe のフォルダに書き込めないため、従来の場所を使う（{directory}）。理由: {writeError}");
                return new DataLocationResult(directory, LocalFallbackReason.NotWritable, notes);
            }

            default:
            {
                var source = placement == DataPlacement.MigrateFromLocalAppData ? local : legacy;
                var result = DataMigration.Copy(source, target, DateTimeOffset.Now);

                if (result.Succeeded)
                {
                    notes.Add($"保存先: {source} から {target} へ引っ越した（元は残してある）。{result.Message}");
                    return new DataLocationResult(target, LocalFallbackReason.None, notes);
                }

                // 写せなかった。今回は元の場所のまま起動し、次の起動でもう一度試す
                var directory = ResolveLocalAppData(localAppData);
                notes.Add($"保存先: {source} から {target} への引っ越しに失敗したので、今回は従来の場所を使う（{directory}）。理由: {result.Message}");
                return new DataLocationResult(directory, LocalFallbackReason.MigrationFailed, notes);
            }
        }
    }

    /// <summary>
    /// 従来の場所のフォルダ。旧い名前（SlideinaCalendar）のフォルダしか無ければ、名前を付け替える。
    /// <para>
    /// 同じドライブの中なので、名前を付け替えるだけで中身は動かない。
    /// <b>付け替えられなかったときは旧い場所をそのまま使う。</b>アプリの名前が変わったという
    /// 都合で、書いてきたものを見失わせるわけにはいかない。
    /// </para>
    /// </summary>
    internal static string ResolveLocalAppData(string localAppData)
    {
        var target = System.IO.Path.Combine(localAppData, CalendarDatabase.FolderName);
        var legacy = System.IO.Path.Combine(localAppData, CalendarDatabase.LegacyFolderName);

        // 新しい側がもう在るなら引っ越しは済んでいる。旧い側が無いなら
        // 引っ越すものが無い（新しく入れた人）
        if (System.IO.Directory.Exists(target) || !System.IO.Directory.Exists(legacy)) return target;

        try
        {
            System.IO.Directory.Move(legacy, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }

    private static bool HasData(string folder) =>
        File.Exists(System.IO.Path.Combine(folder, DatabaseFileName));

    /// <summary>
    /// 書き込めるか。実際に小さなファイルを作って消して確かめる（権限の表示は当てにならない）。
    /// <c>data</c> がまだ無ければ、その親（exe のフォルダ）で確かめる。
    /// </summary>
    internal static bool CanWrite(string folder, out string error)
    {
        error = string.Empty;

        try
        {
            var probeFolder = System.IO.Directory.Exists(folder)
                ? folder
                : System.IO.Path.GetDirectoryName(folder) ?? folder;

            var probe = System.IO.Path.Combine(probeFolder, $".kado-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
