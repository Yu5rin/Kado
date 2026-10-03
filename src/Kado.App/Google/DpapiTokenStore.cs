using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kado.Google.OAuth;

namespace Kado.App.Google;

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
public sealed class DpapiTokenStore(string path, Action<string>? log = null) : ITokenStore
{
    /// <summary>
    /// 復号の手がかり。暗号文だけを盗られても、この値を知らなければ戻せない。
    /// <para>秘密ではなく、他のアプリの DPAPI 暗号文と取り違えないための目印。</para>
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kado.GoogleTokens.v1");

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>復号できなかった控えの扱い。一度の失敗では消さない（<see cref="TokenFileQuarantine"/>）。</summary>
    private readonly TokenFileQuarantine _quarantine = new(
        path ?? throw new ArgumentNullException(nameof(path)),
        cipher => JsonSerializer.Deserialize<OAuthTokens>(
            ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)),
        log);

    /// <summary>
    /// 保存されていた控えを、退避して読み直してもなお復号できなかったか（利用者に伝える段階か）。
    /// <para>
    /// Windows パスワードの強制リセットやプロファイル移行のあとは、保存していたトークンが
    /// 二度と復号できなくなる。<see cref="ITokenStore"/> の形は変えず（<c>Load</c> が
    /// <c>null</c> を返すのは「未接続」と区別しない）、この具象型だけにこの印を持たせて、
    /// 呼び出し側（<c>App.xaml.cs</c>）が理由を説明できるようにする。
    /// </para>
    /// <para>
    /// <b>1度目の失敗では立てない。</b>ログオン直後などの一時的な失敗かもしれないので、控えを
    /// 退避して次の起動で読み直し（<see cref="TokenFileQuarantine"/>）、それでも読めなかったときに立てる。
    /// </para>
    /// </summary>
    public bool DecryptionFailed { get; private set; }

    /// <summary>
    /// 復号できた控えを覚えておく。<see cref="Load"/> は起動のうちに何度も呼ばれ
    /// （接続の有無の確認）、そのたびに DPAPI を呼んでいた。会社の PC（ドメイン参加）では
    /// DPAPI の最初の呼び出しが遅いことがあり、繰り返すぶんだけ画面が止まる。
    /// <see cref="Save"/> と <see cref="Clear"/> では捨てる。
    /// </summary>
    private OAuthTokens? _loaded;

    /// <summary>この起動で、控えを読めなかったと分かったか。</summary>
    private bool _unreadableThisRun;

    /// <summary>
    /// 読み込みと控えの出し入れを1本にする。起動時に裏のスレッドで先に読んでおき
    /// （<c>App.xaml.cs</c>）、あとから画面のスレッドが呼んでも二重に復号しない。
    /// </summary>
    private readonly object _gate = new();

    public OAuthTokens? Load()
    {
        lock (_gate)
        {
            if (_loaded is not null) return _loaded;

            // 読めなかったと分かったあとは、この起動のあいだ読み直さない。接続の有無の確認で
            // 何度も呼ばれるので、そのたびに退避した控えを読み直すと、1回の起動の中で
            // 「1度目」と「2度目」が続けて起きてしまう（読み直しは次の起動で行う）
            if (_unreadableThisRun) return null;

            try
            {
                var (tokens, state) = _quarantine.Load();

                if (state == TokenLoadState.Persisting) DecryptionFailed = true;
                if (state != TokenLoadState.Normal) _unreadableThisRun = true;

                return _loaded = tokens;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void Save(OAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        lock (_gate)
        {
            // 書き換えたら、覚えていた控えは古い。書けなかったときも読み直させる
            _loaded = null;
            _unreadableThisRun = false;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var cipher = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(tokens), Entropy, DataProtectionScope.CurrentUser);

            // 書いている途中で落ちても、元の控えを壊さない
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, cipher);
            File.Move(temporary, _path, overwrite: true);

            // つなぎ直した。読めなかった古い控えは、もう要らない
            _quarantine.DeleteQuarantined();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _loaded = null;
            TryDelete();

            // 切断。退避していた読めない控えも、こちらの都合で残さない
            _quarantine.DeleteQuarantined();
        }
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても、呼び出し側は接続を切った（あるいは読めないと知った）つもりでいる。
            // 握り潰さず次へ進める
        }
    }
}
