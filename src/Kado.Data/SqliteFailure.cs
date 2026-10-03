using Microsoft.Data.Sqlite;

namespace Kado.Data;

/// <summary>SQLite の失敗の大まかな種類。利用者に出す文言と、やり直してよいかの判断に使う。</summary>
public enum SqliteFailureKind
{
    /// <summary>ほかのプロセス（や別の接続）が使っていて、待っても開かなかった。一時的。</summary>
    Busy,

    /// <summary>ファイルが壊れている、あるいは SQLite のファイルではない。</summary>
    Corrupt,

    /// <summary>ディスクがいっぱい。</summary>
    DiskFull,

    /// <summary>書き込めない（読み取り専用、権限なし）。</summary>
    ReadOnly,

    /// <summary>開けない・読み書きの途中で I/O が失敗した。</summary>
    Io,

    /// <summary>上のどれでもない。</summary>
    Other,
}

/// <summary>
/// <see cref="SqliteException"/> の見分けと、利用者向けの文言。
/// <para>
/// 見分けを App 側に散らさず、Linux でテストできるここに寄せてある。
/// <b>「ロックされている」と「壊れている」は別物</b>で、前者を壊れた扱いにして
/// 健全なデータベースを退避させてはいけない。
/// </para>
/// </summary>
public static class SqliteFailure
{
    // SQLite の一次結果コード（https://www.sqlite.org/rescode.html）。
    // 拡張コードは下位8ビットが一次コードなので、マスクして比べる
    private const int Perm = 3;
    private const int Busy = 5;
    private const int Locked = 6;
    private const int ReadOnly = 8;
    private const int IoErr = 10;
    private const int Corrupt = 11;
    private const int Full = 13;
    private const int CantOpen = 14;
    private const int Auth = 23;
    private const int NotADb = 26;

    /// <summary>種類を見分ける。</summary>
    public static SqliteFailureKind Classify(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return (exception.SqliteErrorCode & 0xFF) switch
        {
            Busy or Locked => SqliteFailureKind.Busy,
            Corrupt or NotADb => SqliteFailureKind.Corrupt,
            Full => SqliteFailureKind.DiskFull,
            ReadOnly or Perm or Auth => SqliteFailureKind.ReadOnly,
            IoErr or CantOpen => SqliteFailureKind.Io,
            _ => SqliteFailureKind.Other,
        };
    }

    /// <summary>ほかのものが使っているだけか（待てば開く見込みがある）。</summary>
    public static bool IsBusy(SqliteException exception) => Classify(exception) == SqliteFailureKind.Busy;

    /// <summary>本当に壊れているか。ロックや I/O の失敗は含めない。</summary>
    public static bool IsCorrupt(SqliteException exception) => Classify(exception) == SqliteFailureKind.Corrupt;

    /// <summary>
    /// 利用者の側で直せる（待つ・空きを作る・保存先を変える）種類か。
    /// <para>
    /// 画面のスレッドで漏れてきたとき、アプリごと終わらせず案内して続けてよいかの判断に使う。
    /// 壊れている・想定外のものは、続けるとかえって傷を広げるので含めない。
    /// </para>
    /// </summary>
    public static bool IsRecoverable(SqliteException exception) =>
        Classify(exception) is SqliteFailureKind.Busy or SqliteFailureKind.DiskFull
            or SqliteFailureKind.ReadOnly or SqliteFailureKind.Io;

    /// <summary>
    /// 書き込みに失敗したときの文言。
    /// <para>例外の型名は出さない。種類ごとに次の一手が分かるように添える。</para>
    /// </summary>
    public static string DescribeSaveFailure(SqliteException exception) => Classify(exception) switch
    {
        SqliteFailureKind.Busy =>
            "保存できませんでした（ほかのアプリがデータを使っています。少し待ってからやり直してください）",
        SqliteFailureKind.DiskFull =>
            "保存できませんでした（ディスクの空きが足りません）",
        SqliteFailureKind.ReadOnly =>
            "保存できませんでした（データの保存先に書き込めません。ほかのアプリの使用や権限を確かめてください）",
        SqliteFailureKind.Corrupt =>
            "保存できませんでした（データが壊れている可能性があります。バックアップからの復元を検討してください）",
        _ => "保存できませんでした（ディスクの空き・ほかのアプリの使用を確かめてください）",
    };

    /// <summary>
    /// バックアップ・取り込み・書き出しなど、ファイルを相手にした操作が SQLite の失敗で止まったときの文言の「理由」の部分。
    /// </summary>
    public static string DescribeReason(SqliteException exception) => Classify(exception) switch
    {
        SqliteFailureKind.Busy => "ほかのアプリがファイルを使っています",
        SqliteFailureKind.DiskFull => "ディスクの空きが足りません",
        SqliteFailureKind.ReadOnly => "書き込めない場所です。保存先を変えてください",
        SqliteFailureKind.Corrupt => "Kado のデータとして読めないファイルです",
        SqliteFailureKind.Io => "ファイルを開けない、または読み書きできません。保存先を確かめてください",
        _ => "データベースの操作に失敗しました",
    };
}
