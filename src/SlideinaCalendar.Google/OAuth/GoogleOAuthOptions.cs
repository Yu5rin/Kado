namespace SlideinaCalendar.Google.OAuth;

/// <summary>
/// OAuth の設定。
/// <para>
/// <c>chrome.identity</c> は使えないので、Google Cloud Console で「デスクトップアプリ」
/// 型のクライアント ID を発行し、ループバックリダイレクトで受ける（要件書 6.2）。
/// 拡張機能用の ID は流用できない。
/// </para>
/// </summary>
public sealed record GoogleOAuthOptions
{
    /// <summary>現行踏襲のスコープ（要件書 6.1）。</summary>
    public static readonly IReadOnlyList<string> DefaultScopes =
    [
        "https://www.googleapis.com/auth/calendar.events",
        "https://www.googleapis.com/auth/calendar.readonly",
        "https://www.googleapis.com/auth/calendar.app.created",
        "https://www.googleapis.com/auth/tasks",
    ];

    /// <summary>クライアント ID。</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// クライアントシークレット。
    /// <para>
    /// デスクトップアプリ型では秘密として扱えない（配布物から読める）。Google も
    /// そう明記している。PKCE があるので、これだけでは認可コードを交換できない。
    /// </para>
    /// </summary>
    public required string ClientSecret { get; init; }

    /// <summary>要求するスコープ。</summary>
    public IReadOnlyList<string> Scopes { get; init; } = DefaultScopes;

    /// <summary>認可画面の URL。</summary>
    public Uri AuthorizationEndpoint { get; init; } = new("https://accounts.google.com/o/oauth2/v2/auth");

    /// <summary>トークンの交換先。</summary>
    public Uri TokenEndpoint { get; init; } = new("https://oauth2.googleapis.com/token");

    /// <summary>取り消し先。接続を切るときに使う。</summary>
    public Uri RevocationEndpoint { get; init; } = new("https://oauth2.googleapis.com/revoke");
}
