namespace Kado.Google.OAuth;

/// <summary>
/// 受け取ったトークン。
/// <para>
/// 更新トークンは<b>初回の認可でしか返らない</b>ことがある。更新のたびに上書きすると
/// 失って、次回から黙って認証を求めることになる。<see cref="WithRefreshed"/> を通す。
/// </para>
/// </summary>
public sealed record OAuthTokens
{
    /// <summary>API を呼ぶためのトークン。</summary>
    public required string AccessToken { get; init; }

    /// <summary>取り直すためのトークン。初回の認可でだけ返ることがある。</summary>
    public string? RefreshToken { get; init; }

    /// <summary>失効する時刻。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>許可されたスコープ。要求したものと違うことがある。</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>
    /// 失効まで余裕があるか。
    /// <para>ぴったりで判定すると、呼び出しの最中に切れる。少し手前で切り上げる。</para>
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now, TimeSpan? margin = null) =>
        now + (margin ?? TimeSpan.FromMinutes(2)) < ExpiresAt;

    /// <summary>
    /// 取り直した結果を重ねる。
    /// <para>更新トークンが返らなかったときは、いま持っているものを残す。</para>
    /// </summary>
    public OAuthTokens WithRefreshed(OAuthTokens refreshed)
    {
        ArgumentNullException.ThrowIfNull(refreshed);

        return refreshed with
        {
            RefreshToken = refreshed.RefreshToken ?? RefreshToken,
            Scopes = refreshed.Scopes.Count > 0 ? refreshed.Scopes : Scopes,
        };
    }
}
