using System.IO;
using System.Security.Cryptography;
using Kado.Google.OAuth;

namespace Kado.App.Google;

/// <summary>トークンの控えを読んだ結果の状態。</summary>
internal enum TokenLoadState
{
    /// <summary>読めた、あるいは控えが無い（未接続）。</summary>
    Normal,

    /// <summary>
    /// 初めて読めなかった。控えを消さずに退避した（<c>.unreadable</c>）。一時的な失敗かもしれないので、
    /// 利用者には伝えない。次回以降の起動で読み直す。
    /// </summary>
    FirstFailure,

    /// <summary>
    /// 退避したあとの読み直しでも読めなかった。ここで一度だけ利用者に伝える。
    /// </summary>
    Persisting,

    /// <summary>読めない状態が続いているが、すでに伝えた。黙って未接続として扱う。</summary>
    PersistingAnnounced,
}

/// <summary>
/// 復号できなかったトークンの控えの扱い。
/// <para>
/// <b>一度の失敗で控えを消さない。</b>DPAPI は、ログオン直後（プロファイルや鍵がまだ使えない）や
/// ドメインの接続の都合で、一時的に失敗することがある。すぐ消すと、直るはずだった接続情報まで失い、
/// 毎回つなぎ直しになる。そこで、読めなかった控えは名前を変えて取っておき、次の起動で読み直す。
/// 読めたら元に戻して続ける。読めないままなら、その時に一度だけ伝える。
/// </para>
/// <para>
/// 復号そのものは <c>decode</c> として受け取る（DPAPI は Windows でしか動かない）ので、
/// 状態の移り変わりを Linux で試験できる。
/// </para>
/// </summary>
internal sealed class TokenFileQuarantine(string path, Func<byte[], OAuthTokens?> decode, Action<string>? log = null)
{
    /// <summary>1度目に読めなかった控えの退避先の接尾辞。</summary>
    internal const string UnreadableSuffix = ".unreadable";

    /// <summary>読み直しても読めず、利用者に伝えたあとの退避先の接尾辞。</summary>
    internal const string AnnouncedSuffix = ".unreadable-announced";

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private readonly Func<byte[], OAuthTokens?> _decode = decode ?? throw new ArgumentNullException(nameof(decode));
    private readonly Action<string> _log = log ?? (_ => { });

    private string Unreadable => _path + UnreadableSuffix;

    private string Announced => _path + AnnouncedSuffix;

    /// <summary>読む。読めなければ null と、その理由の状態を返す。</summary>
    /// <exception cref="IOException">控えのファイルそのものを読めない（復号の失敗ではないもの）。</exception>
    internal (OAuthTokens? Tokens, TokenLoadState State) Load()
    {
        if (File.Exists(_path)) return LoadLive();

        return LoadQuarantined();
    }

    /// <summary>書き込みや切断のあと、退避しておいた古い控えを片付ける（もう要らない）。</summary>
    internal void DeleteQuarantined()
    {
        TryDelete(Unreadable);
        TryDelete(Announced);
    }

    private (OAuthTokens?, TokenLoadState) LoadLive()
    {
        try
        {
            var tokens = _decode(File.ReadAllBytes(_path));

            // 読めた。前に退避したものは、もう要らない
            DeleteQuarantined();
            return (tokens, TokenLoadState.Normal);
        }
        catch (CryptographicException ex)
        {
            // すでに退避したことがあるなら、これは2度目以降の失敗
            var second = File.Exists(Unreadable) || File.Exists(Announced);

            _log($"トークンの復号: 失敗（{(second ? "2度目以降" : "1度目")}）。{ex.GetType().Name}");

            if (!TryMove(_path, second ? Announced : Unreadable))
            {
                // 退避できなければ、そのまま残る。次の起動でもう一度ここへ来る（消しはしない）
                return (null, TokenLoadState.FirstFailure);
            }

            return second
                ? (null, TokenLoadState.Persisting)
                : (null, TokenLoadState.FirstFailure);
        }
    }

    private (OAuthTokens?, TokenLoadState) LoadQuarantined()
    {
        var (file, alreadyAnnounced) = File.Exists(Unreadable)
            ? (Unreadable, false)
            : File.Exists(Announced) ? (Announced, true) : (null, false);

        if (file is null) return (null, TokenLoadState.Normal);

        try
        {
            var tokens = _decode(File.ReadAllBytes(file));

            // 読めた。一時的な失敗だった。元の名前へ戻して続ける
            if (TryMove(file, _path))
            {
                _log("トークンの復号: 退避していた控えを読めたので元に戻しました");
                return (tokens, TokenLoadState.Normal);
            }

            // 戻せなくても、この起動のあいだは使える
            return (tokens, TokenLoadState.Normal);
        }
        catch (CryptographicException)
        {
            if (alreadyAnnounced) return (null, TokenLoadState.PersistingAnnounced);

            // 退避したあとの読み直しでも読めなかった。一度だけ伝える
            _log("トークンの復号: 読み直しでも読めませんでした");
            TryMove(file, Announced);
            return (null, TokenLoadState.Persisting);
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても進める
        }
    }
}
