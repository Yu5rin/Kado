using System.Text.Json;

namespace Kado.Presentation.Update;

/// <summary>見つかった新しい版。</summary>
/// <param name="Version">版。</param>
/// <param name="TagName">もとのタグ名（<c>v0.5.0</c> など）。</param>
/// <param name="DownloadUrl">実行ファイルの取得先。</param>
/// <param name="SizeBytes">大きさ。進み具合の表示に使う。</param>
/// <param name="Sha256">ハッシュ。付いていなければ null。</param>
/// <param name="ReleaseUrl">リリースのページ。</param>
/// <param name="ReleaseNotes">何が変わったか。</param>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256,
    string ReleaseUrl,
    string ReleaseNotes);

/// <summary>
/// リリースの応答を読む。
/// <para>
/// <b>落としたものをそのまま実行する</b>ので、ここの判断を誤ると、意図しない場所から
/// 取ってきた実行ファイルを動かすことになる。通信も画面も持たない純粋な処理にして、
/// 判断の中身を試せるようにしてある。
/// </para>
/// </summary>
public static class ReleaseFeed
{
    /// <summary>
    /// リリースの JSON から、Windows 向けの実行ファイルを探す。
    /// <para>読めない・条件に合わないときは null。</para>
    /// </summary>
    public static UpdateInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            if (Text(root, "tag_name") is not { } tag || !TryParseVersion(tag, out var version)) return null;

            // 下書きと事前公開は配らない
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;

                if (Text(asset, "name") is not { } name ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 応答に書かれた行き先を鵜呑みにしない
                if (Text(asset, "browser_download_url") is not { } url || !IsAllowedDownloadUrl(url)) continue;

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

                return new UpdateInfo(
                    version, tag, url, size, ReadSha256(asset),
                    Text(root, "html_url") ?? string.Empty,
                    Text(root, "body") ?? string.Empty);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取りに行ってよい URL か。
    /// <para>HTTPS で、GitHub の配信先であること。</para>
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;

        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>v0.5.0</c> のようなタグから版を取り出す。
    /// <para><c>0.5.0-beta</c> のような後ろ側は落とす。</para>
    /// </summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (tag is not { Length: > 0 }) return false;

        var text = tag.TrimStart('v', 'V');

        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];

        return Version.TryParse(text, out version!);
    }

    /// <summary>
    /// 入れ替えるべきか。
    /// <para>
    /// <b>版は必ず数として比べる。</b>文字で比べると <c>0.4.10</c> が <c>0.4.9</c> より
    /// 古いことになり、更新が止まる。
    /// </para>
    /// </summary>
    public static bool IsNewerThan(UpdateInfo info, Version current)
    {
        ArgumentNullException.ThrowIfNull(info);

        return info.Version > current;
    }

    // ------------------------------------------------------------------

    /// <summary>GitHub は <c>sha256:…</c> の形で付けてくることがある。</summary>
    private static string? ReadSha256(JsonElement asset) =>
        Text(asset, "digest") is { } digest &&
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
