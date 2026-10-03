using System.IO;
using Kado.Data;

namespace Kado.App.Shell;

/// <summary>
/// スライド・ピン留めまわりの実測値を記録する（切り分け用）。
/// <para>
/// 不具合1（反対側から出る）は静的に追っても原因が確定していない。実機でしか
/// 起きないので、値を残せるようにしておく。<c>crash.log</c>（<c>App.xaml.cs</c>）と
/// 同じく、データベースと同じ場所に置く。
/// </para>
/// <para>
/// <b>呼んだスレッドでは行列に積むだけ。</b>ファイルへ書くのは別のスレッド
/// （<see cref="BufferedLogWriter"/>）で、まとめて書く。ウイルス対策ソフトのファイル検査が
/// ある PC で、画面のスレッドが書き込みのたびに止まらないため。そのかわり書き込みは少し遅れるので、
/// <b>終わるとき・異常終了のときは <see cref="Flush"/> を呼ぶ</b>（<c>App.OnExit</c>・
/// <c>ReportFatal</c>。プロセスの終了でも自動で呼ぶ）。
/// </para>
/// <para>
/// 世代は <c>shell.log</c>・<c>.1</c>・<c>.2</c> の3つ、1つ1MBまで。以前は256KBで1世代だけだったので、
/// 窓の幅をしばらくつまむだけで、切り分けに要る記録（起動の計時・更新・Google・配信）が消えた。
/// </para>
/// <para>
/// <b>記録に失敗してもアプリの動作に影響させない。</b>例外はすべて握りつぶす。
/// <b>個人情報や予定の内容は書かない。数値と座標だけ。</b>
/// </para>
/// </summary>
internal static class ShellDiagnosticsLog
{
    /// <summary>1つのファイルの上限。</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>残す世代（現行を含む）。</summary>
    private const int Generations = 3;

    private static readonly Lazy<BufferedLogWriter> Writer = new(Create);

    private static BufferedLogWriter Create()
    {
        var writer = new BufferedLogWriter(() => LogPath, MaxBytes, Generations);

        // 普通に終わるときの取りこぼしを防ぐ最後の砦。異常終了は ReportFatal が Flush する
        AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush();

        return writer;
    }

    /// <summary>データベースと同じ場所に置く（crash.log と同じ置き場所の考え方）。</summary>
    private static string LogPath => Path.Combine(
        Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "shell.log");

    /// <summary>1行、時刻付きで書き足す（行列に積むだけ）。失敗しても投げない。</summary>
    internal static void Write(string message)
    {
        try
        {
            Writer.Value.Enqueue(message);
        }
        catch
        {
            // 記録できなくても、シェルの動作は止めない
        }
    }

    /// <summary>
    /// 高頻度に来る行を、間引いて書き足す。
    /// <para>
    /// 同じ <paramref name="key"/> の行は、1秒あたり1行だけ書く。省いた分は、流れが止まったとき
    /// 「n 回省略、これが最後の値」の1行にまとめる。窓の移動通知や、演出のコマごとの値のように、
    /// 全部残すとログが埋まる行に使う。<b>切り分けに要る行（起動・更新・通信・失敗）には使わない。</b>
    /// </para>
    /// </summary>
    internal static void WriteThrottled(string key, string message)
    {
        try
        {
            Writer.Value.Enqueue(message, key);
        }
        catch
        {
            // 記録できなくても、シェルの動作は止めない
        }
    }

    /// <summary>行列に残っている行を、いまのスレッドで書き切る。終わるとき・異常終了のときに呼ぶ。</summary>
    internal static void Flush()
    {
        try
        {
            // 一度も書いていなければ、書き手を作る必要も無い
            if (Writer.IsValueCreated) Writer.Value.Flush();
        }
        catch
        {
            // 終了の邪魔はしない
        }
    }
}
