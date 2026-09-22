using System.IO;
using Kado.Data;

namespace Kado.App.Shell;

/// <summary>
/// スライド・ピン留めまわりの実測値を記録する（切り分け用）。
/// <para>
/// 不具合1（反対側から出る）は静的に追っても原因が確定していない。実機でしか
/// 起きないので、値を残せるようにしておく。<c>crash.log</c>（<c>App.xaml.cs</c>）と
/// 同じ考え方（世代は1つだけ持てば足りる）に合わせるが、置き場は App 側を
/// 触らずに済むよう Shell/ 配下に閉じている。
/// </para>
/// <para>
/// <b>記録に失敗してもアプリの動作に影響させない。</b>例外はすべて握りつぶす。
/// <b>個人情報や予定の内容は書かない。数値と座標だけ。</b>
/// </para>
/// </summary>
internal static class ShellDiagnosticsLog
{
    /// <summary>
    /// これを超えたら世代を1つずらす。
    /// <para><c>crash.log</c>（1MB）より小さくてよい。1回のスライドで数行しか増えない。</para>
    /// </summary>
    private const long MaxBytes = 256 * 1024;

    /// <summary>複数のタイマー・イベントから同時に書きに来ても壊れないように。</summary>
    private static readonly object Gate = new();

    /// <summary>データベースと同じ場所に置く（crash.log と同じ置き場所の考え方）。</summary>
    private static string LogPath => Path.Combine(
        Path.GetDirectoryName(CalendarDatabase.DefaultPath)!, "shell.log");

    /// <summary>1行、時刻付きで書き足す。失敗しても投げない。</summary>
    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfTooBig(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
            }
        }
        catch
        {
            // 記録できなくても、シェルの動作は止めない
        }
    }

    /// <summary>上限を超えていたら <c>shell.log.1</c> へ退避して、新しく書き始める。</summary>
    private static void RotateIfTooBig(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length < MaxBytes) return;

        var previous = path + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(path, previous);
    }
}
