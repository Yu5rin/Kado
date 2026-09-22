using System.Reflection;

namespace Kado.Google.OAuth;

/// <summary>
/// アプリに焼き込んである既定のクライアント設定。
/// <para>
/// <b>利用者に Google Cloud Console を触らせない。</b>プロジェクトを作り、API を2つ有効にし、
/// 同意画面を整え、クライアントを発行して JSON を落として読み込ませる——これは作る側の
/// 仕事であって、使う側に求めるものではない。使う側がするのは「接続」を押すことだけ。
/// </para>
/// <para>
/// 値はビルドのときに埋める（<c>-p:GoogleClientId=…</c>）。ソースには書かないので、
/// リポジトリを公開しても出ない。埋めずに組み立てれば、これまでどおりファイルを読む。
/// </para>
/// <para>
/// <b>配布物からは読める。</b>デスクトップアプリ型のシークレットは秘密として扱えず、
/// Google もそう明記している。横取りへの備えは PKCE が受け持つので、シークレットだけでは
/// 認可コードを交換できない。守るべきは認可のあとに受け取るトークンのほう。
/// </para>
/// </summary>
public static class EmbeddedClientSettings
{
    private const string ClientIdKey = "GoogleClientId";
    private const string ClientSecretKey = "GoogleClientSecret";

    private static readonly Lazy<GoogleOAuthOptions?> Value = new(Build);

    /// <summary>焼き込んである設定。入っていなければ null。</summary>
    public static GoogleOAuthOptions? Options => Value.Value;

    /// <summary>焼き込んであるか。</summary>
    public static bool Exists => Value.Value is not null;

    private static GoogleOAuthOptions? Build()
    {
        var id = Read(ClientIdKey);
        var secret = Read(ClientSecretKey);

        // 片方でも欠けていれば、焼き込まれていないものとして扱う。
        // 半端な値で繋ぎにいくと、認可が通らない形でしか失敗が現れない
        if (id is not { Length: > 0 } || secret is not { Length: > 0 }) return null;

        return new GoogleOAuthOptions { ClientId = id, ClientSecret = secret };
    }

    /// <summary>組み立てのときに埋めた値を読む。</summary>
    private static string? Read(string key) =>
        typeof(EmbeddedClientSettings).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
            ? value
            : null;
}
