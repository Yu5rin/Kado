using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlideinaCalendar.Google.OAuth;

namespace SlideinaCalendar.App.Google;

/// <summary>
/// トークンを Windows の DPAPI で守って保存する。
/// <para>
/// <b>守るべきはトークンのほう。</b>クライアント ID とシークレットは、デスクトップ
/// アプリ型である以上どのみち利用者の手元に置かれ、秘密として扱えない。対して
/// 更新トークンは、これ1本でカレンダーとタスクを読み書きできてしまう。
/// </para>
/// <para>
/// <c>CurrentUser</c> で保護するので、<b>同じ PC の同じ Windows ユーザーでしか
/// 復号できない</b>。ファイルごと別の PC へ写しても読めない。
/// </para>
/// </summary>
public sealed class DpapiTokenStore(string path) : ITokenStore
{
    /// <summary>
    /// 復号の手がかり。暗号文だけを盗られても、この値を知らなければ戻せない。
    /// <para>秘密ではなく、他のアプリの DPAPI 暗号文と取り違えないための目印。</para>
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SlideinaCalendar.GoogleTokens.v1");

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    public OAuthTokens? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<OAuthTokens>(plain);
        }
        catch (CryptographicException)
        {
            // 別のユーザーや別の PC の控え。読めないものは無いものとして扱い、繋ぎ直させる
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(OAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var cipher = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(tokens), Entropy, DataProtectionScope.CurrentUser);

        // 書いている途中で落ちても、元の控えを壊さない
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, cipher);
        File.Move(temporary, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても、呼び出し側は接続を切ったつもりでいる。握り潰さず次へ進める
        }
    }
}
