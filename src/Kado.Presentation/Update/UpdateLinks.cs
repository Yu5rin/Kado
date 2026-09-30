using System.Text.RegularExpressions;

namespace Kado.Presentation.Update;

/// <summary>
/// 更新の問い合わせ先と配布物の場所を、通信せずに組み立てる。
/// <para>
/// <b>GitHub の API には、未認証だと 1 時間に 60 回の上限がある。</b>上限は端末ごとではなく
/// IP アドレスごとに数えられるので、会社のように同じ出口を大勢で共有する回線では、
/// 他の人の通信で先に使い切られて毎回 403 になる（同じ作者の Pane で実機のログに出た）。
/// そこで、上限を受けない <c>releases.atom</c> で最新のタグだけを見て、配布物の URL は
/// 規則から組み立てられるようにしてある。
/// </para>
/// <para>
/// 通信も画面も持たない純粋な処理にして、判断の中身をテストで固定する。
/// </para>
/// </summary>
public static partial class UpdateLinks
{
    /// <summary>
    /// タグの表記。<c>vX.Y.Z</c>（数字3つ）だけを版のタグとして扱う。
    /// <para>
    /// Atom の応答から来た文字列で配布物の URL を組み立てるので、想定した形以外は
    /// 入口で捨てる。<c>v1.0.0-beta</c> や <c>nightly</c> のようなものは無視される。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^v[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    /// <summary>版のタグ（<c>vX.Y.Z</c>）として読めるか。読めたら版も返す。</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (tag is null || !TagPattern().IsMatch(tag)) return false;

        return Version.TryParse(tag[1..], out version!);
    }

    /// <summary>
    /// API の問い合わせ先から、同じリポジトリの Atom フィードの URL を組み立てる。
    /// 組み立てられない形（GitHub 以外の配布元など）は null。
    /// <code>
    /// https://api.github.com/repos/{owner}/{repo}/releases/latest
    ///   → https://github.com/{owner}/{repo}/releases.atom
    /// </code>
    /// </summary>
    public static string? TryBuildAtomUrl(string? apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        // repos / {owner} / {repo} / releases / latest
        var parts = uri.AbsolutePath.Trim('/').Split('/');

        if (parts.Length < 4 || !parts[0].Equals("repos", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1].Length == 0 || parts[2].Length == 0) return null;

        return $"https://github.com/{parts[1]}/{parts[2]}/releases.atom";
    }

    /// <summary>
    /// 配布物（exe）のファイル名。
    /// <para>
    /// <b><c>.github/workflows/release.yml</c> がこの名前で作る</b>（<c>Kado-{版}-win-x64.exe</c>。
    /// 版にタグの <c>v</c> は付かない）。ワークフロー側で名前を変えるなら、ここも直すこと。
    /// 食い違うと、API が使えないときに組み立てた URL が 404 になる
    /// （<c>UpdateLinksTests</c> がワークフローの記述と突き合わせている）。
    /// </para>
    /// </summary>
    public static string BuildAssetFileName(string tag) => $"Kado-{StripV(tag)}-win-x64.exe";

    /// <summary>
    /// API を使わずに、配布物のダウンロード URL を組み立てる。組み立てられなければ null。
    /// <code>
    /// https://github.com/{owner}/{repo}/releases/download/{tag}/Kado-{版}-win-x64.exe
    /// </code>
    /// <para>
    /// 引き換えに <b>SHA256 は分からない</b>（GitHub が付ける <c>digest</c> は API からしか
    /// 取れない）。照合を省いて続行するのは、もともとある「ハッシュが付いていれば確かめる」形の
    /// 「付いていない」側と同じ。HTTPS で取っている以上、そこで防げるのは転送中の破損だけ、
    /// という点も変わらない。
    /// </para>
    /// </summary>
    public static string? TryBuildDownloadUrl(string? atomUrl, string? tag)
    {
        if (!TryParseTag(tag, out _)) return null;
        if (!TryGetReleasesBase(atomUrl, out var releases)) return null;

        return $"{releases}/download/{Uri.EscapeDataString(tag!)}/{Uri.EscapeDataString(BuildAssetFileName(tag!))}";
    }

    /// <summary>あるタグのリリースのページ。組み立てられなければ空文字。</summary>
    public static string BuildReleasePageUrl(string? atomUrl, string? tag) =>
        TryParseTag(tag, out _) && TryGetReleasesBase(atomUrl, out var releases)
            ? $"{releases}/tag/{Uri.EscapeDataString(tag!)}"
            : string.Empty;

    /// <summary>
    /// いちばん新しいリリースのページ。タグが分からないとき（配布元に繋がらなかったとき）の
    /// 案内先。組み立てられなければ空文字。
    /// </summary>
    public static string BuildLatestReleasePageUrl(string? atomUrl) =>
        TryGetReleasesBase(atomUrl, out var releases) ? $"{releases}/latest" : string.Empty;

    /// <summary><c>https://github.com/{owner}/{repo}/releases</c>（<c>.atom</c> を落としたもの）。</summary>
    private static bool TryGetReleasesBase(string? atomUrl, out string releases)
    {
        releases = string.Empty;

        if (atomUrl is not { Length: > 0 } ||
            !atomUrl.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        releases = atomUrl[..^".atom".Length];
        return true;
    }

    private static string StripV(string tag) =>
        tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;
}
