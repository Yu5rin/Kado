using System.ComponentModel;
using System.Diagnostics;

namespace Kado.Presentation.Links;

/// <summary>何を起動するか。</summary>
public enum LaunchKind
{
    /// <summary>シェルに任せて開く（URL は既定のブラウザ、ファイルは関連付けのアプリ、フォルダはエクスプローラー）。</summary>
    Shell,

    /// <summary>エクスプローラーで、入っているフォルダを開いてそのファイルを選んだ状態にする。</summary>
    Reveal,
}

/// <summary>起動の依頼1件。何を・どの形で起動するかだけを持つ（判断は <see cref="PathLaunchPlanner"/> などが済ませてある）。</summary>
/// <param name="Kind">起動の形。</param>
/// <param name="Target">URL またはパス。</param>
public sealed record LaunchRequest(LaunchKind Kind, string Target);

/// <summary>
/// URL・ファイル・フォルダを実際に開く口。
/// <para>
/// ViewModel から直接 <c>Process.Start</c> を呼ぶとテストのたびにブラウザやエクスプローラーが
/// 立ち上がる。<see cref="Editing.IFileDialogs"/> と同じ考え方で、起動する側を差し替えられるようにする。
/// 何を開いてよいかの判断は呼ぶ前に済ませてあり、ここは言われたとおりに起動するだけ。
/// </para>
/// </summary>
public interface ILinkOpener
{
    /// <summary>起動する。起動できなければ例外（<see cref="Win32Exception"/> など）。</summary>
    void Launch(LaunchRequest request);
}

/// <summary>何も起動しない実装。開く口を用意していない組み立て方（テストなど）で使う。</summary>
public sealed class NullLinkOpener : ILinkOpener
{
    public static readonly NullLinkOpener Instance = new();

    private NullLinkOpener() { }

    public void Launch(LaunchRequest request) { }
}

/// <summary>
/// <c>Process.Start</c> で実際に起動する実装（Windows）。
/// <para>
/// <see cref="LaunchKind.Shell"/> は <c>UseShellExecute = true</c>。<see cref="LaunchKind.Reveal"/> は
/// <c>explorer.exe /select,"パス"</c>。パスの引用は <see cref="PathLaunchPlanner.RevealArguments"/> が
/// 決める（<c>ArgumentList</c> に任せると explorer が読めない形に引用される）。
/// </para>
/// </summary>
public sealed class ShellLinkOpener : ILinkOpener
{
    public void Launch(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var start = request.Kind switch
        {
            LaunchKind.Reveal => new ProcessStartInfo("explorer.exe")
            {
                Arguments = PathLaunchPlanner.RevealArguments(request.Target),
                UseShellExecute = false,
            },
            _ => new ProcessStartInfo(request.Target) { UseShellExecute = true },
        };

        // 起動したプロセスは追わない。ハンドルだけ返す
        using var _ = Process.Start(start);
    }
}
