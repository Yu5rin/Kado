using System.Reflection;
using Kado.Data;
using Microsoft.Data.Sqlite;

namespace Kado.Presentation.Infrastructure;

/// <summary>
/// 画面のスレッドで、誰にも受けられずに漏れてきた例外の扱い。
/// <para>
/// 想定外の例外は「予期しないエラー」を出して終わる（壊れた状態で動き続けるより安全）。
/// ただし、利用者の側で直せる失敗（ディスクがいっぱい・ほかのアプリがデータを使っている）まで
/// アプリごと終わらせると、入力の途中だった内容を失う。入口で受け損ねたものの最後の網として、
/// これだけは案内して続ける。
/// </para>
/// </summary>
public static class UnhandledFailurePolicy
{
    /// <summary>案内して続けてよいか。</summary>
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return Unwrap(exception) is SqliteException sqlite && SqliteFailure.IsRecoverable(sqlite);
    }

    /// <summary>案内の文言。<see cref="IsRecoverable"/> が true のときに使う。</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var reason = Unwrap(exception) is SqliteException sqlite
            ? SqliteFailure.DescribeSaveFailure(sqlite)
            : "保存できませんでした";

        return reason + "\n\nいまの操作は反映されていない可能性があります。原因を取り除いてから、もう一度お試しください。";
    }

    /// <summary>同じ種類の案内を続けて出さない間隔。</summary>
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 案内を出してよいか。
    /// <para>
    /// 画面のタイマー（時計・同期）が、ロックされているあいだ毎回同じ失敗を漏らすことがある。
    /// そのたびに窓を出すと、閉じても閉じても出てきて何もできなくなる。前に出してから
    /// <see cref="NoticeInterval"/> のあいだは出さない（記録には残る）。
    /// </para>
    /// </summary>
    public static bool ShouldNotify(DateTimeOffset? lastNoticeAt, DateTimeOffset now) =>
        lastNoticeAt is not { } last || now - last >= NoticeInterval;

    /// <summary>包んでいる例外（呼び出しの包み・まとめ）を剥がして、中の例外を返す。</summary>
    private static Exception Unwrap(Exception exception)
    {
        var current = exception;

        for (var depth = 0; depth < 8; depth++)
        {
            switch (current)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    current = inner;
                    continue;

                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    current = aggregate.InnerExceptions[0];
                    continue;
            }

            break;
        }

        return current;
    }
}
