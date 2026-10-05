namespace Kado.Presentation.Links;

/// <summary>
/// 開いてよい URL かどうかの決まり。
/// <para>
/// 予定の URL・予定の添付・タスクの URL を開くところ（編集画面と右クリックメニュー）が
/// すべてここを通る。ShellExecute に無検証で文字列を渡すと、<c>file:</c> や <c>ms-msdt:</c> のような
/// 別の仕組みを起こせてしまうため、決まった形の URL だけを渡す。
/// </para>
/// </summary>
public static class LinkRules
{
    /// <summary>
    /// 開いてよいリンクなら、ブラウザに渡す形（正規化した絶対 URL）を返す。違えば null。
    /// <para>通すのは <c>http</c> と <c>https</c> だけ。</para>
    /// </summary>
    public static string? WebUrl(string? text) => Normalize(text, allowHttp: true);

    /// <summary>
    /// 開いてよい添付（Google ドライブのファイルなど）の URL なら、ブラウザに渡す形を返す。違えば null。
    /// <para>通すのは <c>https</c> だけ。</para>
    /// </summary>
    public static string? SecureUrl(string? text) => Normalize(text, allowHttp: false);

    /// <summary>https の URL か。</summary>
    public static bool IsHttps(string? text) => SecureUrl(text) is not null;

    private static string? Normalize(string? text, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();

        // 制御文字（改行など）を含むものは、ブラウザに渡す前に止める
        if (trimmed.Any(char.IsControl)) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isHttp = uri.Scheme == Uri.UriSchemeHttp;

        if (!(isHttps || (allowHttp && isHttp))) return null;

        // ホストの無い形（"http:///x"）は開けない
        return uri.Host.Length == 0 ? null : uri.AbsoluteUri;
    }
}
