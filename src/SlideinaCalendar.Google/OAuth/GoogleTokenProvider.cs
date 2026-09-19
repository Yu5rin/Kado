namespace SlideinaCalendar.Google.OAuth;

/// <summary>
/// アクセストークンを配る。
/// <para>
/// 失効が近ければ黙って取り直す。同時に何本も走っても、取り直しは1回で済ませる。
/// 同期は予定とタスクで独立した経路を持つ（要件書 6.3）ので、並行して呼ばれる。
/// </para>
/// </summary>
public sealed class GoogleTokenProvider(
    LoopbackOAuthFlow flow, ITokenStore store, TimeProvider? time = null)
    : Sync.IAccessTokenSource, IDisposable
{
    private readonly LoopbackOAuthFlow _flow = flow ?? throw new ArgumentNullException(nameof(flow));
    private readonly ITokenStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>繋いであるか。</summary>
    public bool IsConnected => _store.Load() is not null;

    /// <summary>
    /// 認可を受けて繋ぐ。ブラウザが開く。
    /// <para>すでに繋いであっても、呼べば取り直す（権限を足したときなど）。</para>
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await _flow.AuthorizeAsync(cancellationToken).ConfigureAwait(false);

        if (tokens.RefreshToken is null)
        {
            // これが無いと、次に開くたびにブラウザが立ち上がる
            throw new OAuthException("更新トークンを受け取れませんでした。もう一度お試しください。");
        }

        _store.Save(tokens);
    }

    /// <summary>接続を切る。Google 側の許可も取り消す。</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_store.Load() is { } tokens)
        {
            await _flow.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);
        }

        // 取り消しに失敗しても、こちらの控えは消す。残すと繋がっているように見える
        _store.Clear();
    }

    /// <summary>API を呼ぶためのトークンを取る。必要なら取り直す。</summary>
    /// <exception cref="OAuthException">繋いでいない、または取り直せない。</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_store.Load() is not { } tokens) throw new OAuthException("Google に接続していません。");
        if (tokens.IsUsableAt(_time.GetUtcNow())) return tokens.AccessToken;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 待っている間に別の呼び出しが取り直していることがある
            if (_store.Load() is { } latest && latest.IsUsableAt(_time.GetUtcNow())) return latest.AccessToken;

            var current = _store.Load() ?? throw new OAuthException("Google に接続していません。");
            if (current.RefreshToken is not { } refreshToken)
            {
                throw new OAuthException("更新トークンがありません。接続し直してください。");
            }

            OAuthTokens refreshed;
            try
            {
                refreshed = current.WithRefreshed(
                    await _flow.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false));
            }
            catch (OAuthException e) when (e.IsRefreshTokenDead)
            {
                // 更新トークンが死ぬ場面は現実にある。同意画面が「テスト」のままなら
                // 7日で失効するし、利用者が Google 側で許可を取り消すこともある。
                // 控えを残すと、繋がって見えるのに何をしても失敗し続ける
                _store.Clear();

                throw new OAuthException(
                    "Google との連携が切れました。接続し直してください。"
                    + "（同意画面が「テスト」のままだと7日で切れます）", e.Error);
            }

            _store.Save(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
