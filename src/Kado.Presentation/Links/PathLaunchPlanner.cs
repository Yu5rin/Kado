using System.Diagnostics.CodeAnalysis;

namespace Kado.Presentation.Links;

/// <summary>ファイルの場所を開こうとした結果の種類。</summary>
public enum PathLaunchOutcome
{
    /// <summary>フルパスとして読めない・開いてはいけない書き方だった。</summary>
    Invalid,

    /// <summary>ファイルもフォルダも見つからなかった。</summary>
    NotFound,

    /// <summary>ファイルをシェルで開く。</summary>
    OpenFile,

    /// <summary>フォルダをエクスプローラーで開く。</summary>
    OpenFolder,

    /// <summary>実行形式なので実行せず、入っているフォルダを開いてそのファイルを選んだ状態にする。</summary>
    RevealFile,
}

/// <summary>開き方の判断。</summary>
/// <param name="Outcome">結果の種類。</param>
/// <param name="Request">起動の依頼。開かない（<see cref="PathLaunchOutcome.Invalid"/>・<see cref="PathLaunchOutcome.NotFound"/>）なら null。</param>
/// <param name="Message">使う人に出す文言。黙って開けるなら null。</param>
public sealed record PathLaunchPlan(PathLaunchOutcome Outcome, LaunchRequest? Request, string? Message)
{
    /// <summary>何かを起動する計画か。</summary>
    public bool Launches => Request is not null;
}

/// <summary>
/// タスクに添えたファイルの場所を、どう開くか決める。<b>起動はしない純粋な関数</b>。
/// <para>
/// 判断（フルパスか・存在するか・実行形式か・何を起動するか）だけをここに集める。実際の起動は
/// <see cref="ILinkOpener"/>、存在の確認は呼び出し側が渡す関数が受け持つので、Linux で動くテストで
/// 一通り確かめられる。
/// </para>
/// <para>
/// <b>実行形式は実行しない。</b>タスクの添付は人から受け取ったパスを指すことがあり、押しただけで
/// プログラムが走るのは危ない。代わりに、入っているフォルダを開いてそのファイルを選んだ状態にする。
/// </para>
/// </summary>
public static class PathLaunchPlanner
{
    /// <summary>
    /// 実行しない拡張子（小文字、先頭のドット付き）。
    /// <para>
    /// 先頭の19種は決めごと。続く10種は、開くとやはりコードが動く形式（ClickOnce・Java・管理コンソール・
    /// ネットワーク経由で別の場所を起動する <c>.url</c> など）で、同じ理由で足した。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.Ordinal)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".lnk", ".cpl", ".hta", ".reg", ".pif",
        ".application", ".appref-ms", ".gadget", ".jar", ".msc", ".scf", ".url", ".ws", ".wsc", ".psc1",
    };

    /// <summary>
    /// 開き方を決める。
    /// </summary>
    /// <param name="path">開きたい場所（添付に持っているパス）。</param>
    /// <param name="fileExists">ファイルがあるか。</param>
    /// <param name="directoryExists">フォルダがあるか。</param>
    public static PathLaunchPlan Plan(
        string? path, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var trimmed = path?.Trim();

        if (!IsOpenablePath(trimmed))
        {
            return new PathLaunchPlan(
                PathLaunchOutcome.Invalid, null,
                $"開けません（フルパスの場所ではありません）：{path}");
        }

        // フォルダを先に見る。「手順.exe」という名前のフォルダは、実行せず開くだけでよい
        if (directoryExists(trimmed))
        {
            return new PathLaunchPlan(
                PathLaunchOutcome.OpenFolder, new LaunchRequest(LaunchKind.Shell, trimmed), null);
        }

        if (!fileExists(trimmed))
        {
            return new PathLaunchPlan(PathLaunchOutcome.NotFound, null, $"見つかりません：{trimmed}");
        }

        if (IsExecutable(trimmed))
        {
            return new PathLaunchPlan(
                PathLaunchOutcome.RevealFile, new LaunchRequest(LaunchKind.Reveal, trimmed),
                "実行形式のファイルは開かず、入っているフォルダを開きました");
        }

        return new PathLaunchPlan(PathLaunchOutcome.OpenFile, new LaunchRequest(LaunchKind.Shell, trimmed), null);
    }

    /// <summary>
    /// 実行形式の拡張子か。
    /// <para>
    /// Windows は名前の末尾のドットと空白を無視するので（<c>a.exe.</c> は <c>a.exe</c> として動く）、
    /// 落としてから見る。区切りは <c>\</c> も <c>/</c> も読む。
    /// </para>
    /// </summary>
    public static bool IsExecutable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..].TrimEnd('.', ' ');
        var dot = name.LastIndexOf('.');

        return dot >= 0 && ExecutableExtensions.Contains(name[dot..].ToLowerInvariant());
    }

    /// <summary>
    /// <c>explorer.exe</c> に渡す引数。<c>/select,"C:\a b\c.exe"</c>。
    /// <para>パスは常に引用符で包む。引用符そのものを含むパスは <see cref="Plan"/> で止めてある。</para>
    /// </summary>
    public static string RevealArguments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return $"/select,\"{path}\"";
    }

    /// <summary>
    /// 開いてよい書き方のパスか。ドライブ文字から始まるフルパス（<c>C:\...</c>）か、UNC（<c>\\server\share\...</c>）だけ。
    /// <para>
    /// 相対パス・デバイス名（<c>\\.\</c>、<c>\\?\</c>）・代替データストリーム（<c>a.txt:evil.exe</c>）・
    /// ファイル名に使えない文字を含むものは通さない。<b>引用符を含むものも通さない</b>
    /// （<see cref="RevealArguments"/> の引数を壊せてしまう）。
    /// </para>
    /// <para><c>System.IO.Path</c> には頼らない（Linux で動くテストでも同じ結果にするため）。</para>
    /// </summary>
    public static bool IsOpenablePath([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '?' or '*')) return false;

        var isDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        var isUnc = path.StartsWith(@"\\", StringComparison.Ordinal) && IsUncPath(path);

        if (!isDrive && !isUnc) return false;

        // ドライブ文字のコロン以外にコロンがあれば、代替データストリーム
        return path.IndexOf(':', isDrive ? 2 : 0) < 0;
    }

    /// <summary><c>\\server\share</c> の形（サーバーと共有名がある）か。<c>\\.\</c> と <c>\\?\</c> は除く。</summary>
    private static bool IsUncPath(string path)
    {
        var parts = path[2..].Split('\\', StringSplitOptions.None);

        return parts.Length >= 2
            && parts[0].Length > 0 && parts[0] is not ("." or "?")
            && parts[1].Length > 0;
    }
}
