using System.Text.Json;

namespace SlideinaCalendar.Google.OAuth;

/// <summary>
/// Google Cloud Console から落とした <c>client_secret_*.json</c> を読む。
/// <para>
/// クライアント ID とシークレットを手で写させない。長い文字列の写し間違いは
/// 認可が通らない形でしか現れず、原因が分かりにくい。
/// </para>
/// </summary>
public static class GoogleClientSecrets
{
    /// <summary>ファイルから読む。</summary>
    /// <exception cref="FormatException">中身が想定の形ではない。</exception>
    public static GoogleOAuthOptions FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>読み込む。</summary>
    /// <exception cref="FormatException">中身が想定の形ではない。</exception>
    public static GoogleOAuthOptions Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("JSON として読めませんでした。", e);
        }

        using (document)
        {
            var root = document.RootElement;

            // デスクトップアプリ型は installed、ウェブ型は web の下に入る。
            // ウェブ型はループバックで受けられないので、その場で断る
            if (root.TryGetProperty("web", out _))
            {
                throw new FormatException(
                    "ウェブアプリ用の設定です。「デスクトップアプリ」型で作り直してください。");
            }

            var section = root.TryGetProperty("installed", out var installed) ? installed : root;

            var clientId = Text(section, "client_id")
                ?? throw new FormatException("client_id がありません。");
            var clientSecret = Text(section, "client_secret")
                ?? throw new FormatException("client_secret がありません。");

            return new GoogleOAuthOptions
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            };
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// クライアント設定の置き場所。
/// <para>
/// データベースと同じ場所に置く。シークレットと名が付くが、デスクトップアプリ型では
/// 秘密として扱えない（配布物から読める）ものなので、暗号化はしない。
/// 秘密にすべきなのは<b>トークンのほう</b>（<see cref="ITokenStore"/>）。
/// </para>
/// </summary>
public sealed class GoogleClientSecretsStore(string path)
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>読み込んだファイルを置く先。</summary>
    public string Path => _path;

    /// <summary>利用者が自分の設定を置いているか。</summary>
    public bool HasOwnFile => File.Exists(_path);

    /// <summary>
    /// 繋ぐのに使える設定があるか。
    /// <para>
    /// 焼き込んであれば、利用者が何もしなくても true。ここが false になるのは、
    /// 焼き込まずに組み立てた配布物を、設定を入れずに使っているときだけ。
    /// </para>
    /// </summary>
    public bool Exists => HasOwnFile || EmbeddedClientSettings.Exists;

    /// <summary>
    /// 読む。
    /// <para>
    /// 自分で入れた設定があればそちらを使う。無ければアプリに焼き込んである既定を使う。
    /// 自前のプロジェクトで使いたい人が差し替えられる余地を残しつつ、
    /// ふつうは何もしなくてよい、という形にしてある。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">中身が想定の形ではない。</exception>
    public GoogleOAuthOptions? Load() =>
        HasOwnFile ? GoogleClientSecrets.FromFile(_path) : EmbeddedClientSettings.Options;

    /// <summary>
    /// 落としてきたファイルを取り込む。
    /// <para>形を確かめてから置く。壊れたファイルを置くと、次に開いたときに失敗する。</para>
    /// </summary>
    /// <returns>読み取れた設定。</returns>
    /// <exception cref="FormatException">中身が想定の形ではない。</exception>
    public GoogleOAuthOptions Import(string sourcePath)
    {
        var options = GoogleClientSecrets.FromFile(sourcePath);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.Copy(sourcePath, _path, overwrite: true);

        return options;
    }

    /// <summary>
    /// 自分で入れた設定を消す。
    /// <para>消しても、焼き込んである既定に戻るだけで繋げなくなりはしない。</para>
    /// </summary>
    public void Clear()
    {
        if (HasOwnFile) File.Delete(_path);
    }
}
