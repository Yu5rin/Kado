using System.Security.Cryptography;
using System.Text.Json;
using Kado.App.Google;
using Kado.Google.OAuth;

namespace Kado.Shell.Tests;

/// <summary>
/// 復号できなかったトークンの控えの扱い。
/// <para>
/// <b>一度の失敗で消さない。</b>DPAPI はログオン直後などに一時的に失敗することがある。
/// 退避して次の起動で読み直し、それでも読めないときだけ伝える。
/// </para>
/// </summary>
public class TokenFileQuarantineTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kado-token-").FullName;

    /// <summary>復号できるかどうか。DPAPI の代わり。</summary>
    private bool _canDecrypt;

    private string Live => Path.Combine(_folder, "google-tokens.dat");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private TokenFileQuarantine Create() => new(Live, bytes =>
    {
        if (!_canDecrypt) throw new CryptographicException("鍵が使えません");

        return JsonSerializer.Deserialize<OAuthTokens>(bytes);
    });

    private static byte[] TokenBytes() => JsonSerializer.SerializeToUtf8Bytes(new OAuthTokens
    {
        AccessToken = "at",
        RefreshToken = "rt",
        ExpiresAt = DateTimeOffset.UnixEpoch,
    });

    [Fact]
    public void 読めれば何も起きない()
    {
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = true;

        var (tokens, state) = Create().Load();

        Assert.Equal("rt", tokens!.RefreshToken);
        Assert.Equal(TokenLoadState.Normal, state);
        Assert.True(File.Exists(Live));
    }

    [Fact]
    public void 控えが無ければ未接続のまま()
    {
        var (tokens, state) = Create().Load();

        Assert.Null(tokens);
        Assert.Equal(TokenLoadState.Normal, state);
    }

    [Fact]
    public void 初回の失敗では消さずに退避し利用者には伝えない()
    {
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = false;

        var (tokens, state) = Create().Load();

        Assert.Null(tokens);
        Assert.Equal(TokenLoadState.FirstFailure, state);

        // 消していない。名前を変えて取ってある
        Assert.False(File.Exists(Live));
        Assert.True(File.Exists(Live + TokenFileQuarantine.UnreadableSuffix));
    }

    [Fact]
    public void 次の起動で読めたら元に戻して続ける()
    {
        File.WriteAllBytes(Live, TokenBytes());

        _canDecrypt = false;
        Create().Load();

        // 一時的な失敗が直った
        _canDecrypt = true;
        var (tokens, state) = Create().Load();

        Assert.Equal("rt", tokens!.RefreshToken);
        Assert.Equal(TokenLoadState.Normal, state);
        Assert.True(File.Exists(Live));
        Assert.False(File.Exists(Live + TokenFileQuarantine.UnreadableSuffix));
    }

    [Fact]
    public void 次の起動でも読めなければ一度だけ伝える()
    {
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = false;

        Assert.Equal(TokenLoadState.FirstFailure, Create().Load().State);
        Assert.Equal(TokenLoadState.Persisting, Create().Load().State);

        // 伝えたあとは、読めない状態が続いていても黙っている
        Assert.Equal(TokenLoadState.PersistingAnnounced, Create().Load().State);
        Assert.Equal(TokenLoadState.PersistingAnnounced, Create().Load().State);
    }

    [Fact]
    public void 伝えたあとでも読めるようになれば戻す()
    {
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = false;

        Create().Load();
        Create().Load();

        _canDecrypt = true;
        var (tokens, state) = Create().Load();

        Assert.NotNull(tokens);
        Assert.Equal(TokenLoadState.Normal, state);
        Assert.True(File.Exists(Live));
    }

    [Fact]
    public void 読めない控えを残したまま新しい控えが書かれたら失敗は2回目として数える()
    {
        // 1度目の退避があるところへ、また読めない控えが現れた
        File.WriteAllBytes(Live + TokenFileQuarantine.UnreadableSuffix, TokenBytes());
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = false;

        Assert.Equal(TokenLoadState.Persisting, Create().Load().State);
    }

    [Fact]
    public void 退避した控えは書き込みと切断で片付く()
    {
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = false;
        var quarantine = Create();
        quarantine.Load();
        quarantine.Load();

        quarantine.DeleteQuarantined();

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void 読めた控えは前の退避を片付ける()
    {
        File.WriteAllBytes(Live + TokenFileQuarantine.UnreadableSuffix, TokenBytes());
        File.WriteAllBytes(Live, TokenBytes());
        _canDecrypt = true;

        Create().Load();

        Assert.False(File.Exists(Live + TokenFileQuarantine.UnreadableSuffix));
    }
}
