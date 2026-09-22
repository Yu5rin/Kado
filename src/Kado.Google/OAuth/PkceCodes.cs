using System.Security.Cryptography;
using System.Text;

namespace Kado.Google.OAuth;

/// <summary>
/// PKCE の検証子と challenge。
/// <para>
/// デスクトップアプリではクライアントシークレットを秘密にできないため、
/// これが無いと認可コードを横取りされたときに交換されてしまう。
/// </para>
/// </summary>
/// <param name="Verifier">こちらだけが知っている文字列。交換時に送る。</param>
/// <param name="Challenge">認可画面へ送る、検証子の SHA-256。</param>
public sealed record PkceCodes(string Verifier, string Challenge)
{
    /// <summary>challenge の作り方。Google は S256 を推奨している。</summary>
    public const string Method = "S256";

    /// <summary>新しく作る。</summary>
    public static PkceCodes Create()
    {
        // RFC 7636 は 43〜128 文字。32 バイトを base64url にすると 43 文字になる
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new PkceCodes(verifier, challenge);
    }

    /// <summary>URL に載せられる base64。<c>+/=</c> を使わない。</summary>
    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
