namespace SlideinaCalendar.Google.OAuth;

/// <summary>
/// トークンの置き場所。
/// <para>
/// 実装は OS の仕組みで保護する（Windows なら DPAPI）。ここを平文で置くと、
/// 端末を触れる人が誰でもカレンダーとタスクを読める。
/// </para>
/// </summary>
public interface ITokenStore
{
    /// <summary>読む。まだ繋いでいなければ null。</summary>
    OAuthTokens? Load();

    /// <summary>書く。</summary>
    void Save(OAuthTokens tokens);

    /// <summary>消す。接続を切るときに使う。</summary>
    void Clear();
}

/// <summary>その場限りの置き場所。テストと、保存したくない場面で使う。</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private OAuthTokens? _tokens;

    public InMemoryTokenStore(OAuthTokens? initial = null) => _tokens = initial;

    public OAuthTokens? Load() => _tokens;

    public void Save(OAuthTokens tokens) => _tokens = tokens;

    public void Clear() => _tokens = null;
}
