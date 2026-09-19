using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace SlideinaCalendar.Google.OAuth;

/// <summary>認可に失敗した。利用者が断った場合も含む。</summary>
public sealed class OAuthException(string message, string? error = null) : Exception(message)
{
    /// <summary>Google が返したエラー識別子。断られたときは <c>access_denied</c>。</summary>
    public string? Error { get; } = error;

    /// <summary>利用者が認可画面で断ったか。</summary>
    public bool WasDeclined => string.Equals(Error, "access_denied", StringComparison.Ordinal);

    /// <summary>
    /// 更新トークンがもう使えないか。
    /// <para>
    /// 同意画面が「テスト」のままなら7日で失効する。利用者が Google 側で
    /// 許可を取り消したときも同じ。どちらも繋ぎ直すしかない。
    /// </para>
    /// </summary>
    public bool IsRefreshTokenDead => string.Equals(Error, "invalid_grant", StringComparison.Ordinal);
}

/// <summary>
/// ループバックリダイレクトで認可を受ける（要件書 6.2）。
/// <para>
/// 127.0.0.1 の空きポートで待ち受け、規定のブラウザで認可画面を開き、
/// 戻ってきた認可コードをトークンに交換する。
/// </para>
/// </summary>
public sealed class LoopbackOAuthFlow(
    GoogleOAuthOptions options, HttpClient http, Action<string> openBrowser, TimeProvider? time = null)
{
    private readonly GoogleOAuthOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Action<string> _openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));

    /// <summary>失効時刻の起点。トークンを配る側と同じ時計でないと食い違う。</summary>
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// 認可を受けてトークンを取る。
    /// <para>ブラウザが閉じられたままだと戻らないので、呼び出し側で打ち切れるようにしておく。</para>
    /// </summary>
    public async Task<OAuthTokens> AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        var pkce = PkceCodes.Create();
        var state = PkceCodes.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        var redirectUri = StartListener(listener);

        _openBrowser(BuildAuthorizationUrl(redirectUri, pkce.Challenge, state));

        var code = await WaitForCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);

        return await ExchangeAsync(
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["code_verifier"] = pkce.Verifier,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新トークンで取り直す。</summary>
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return ExchangeAsync(
            new Dictionary<string, string>
            {
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            },
            cancellationToken);
    }

    /// <summary>接続を切る。取り消しに失敗しても、こちらの控えは消す。</summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });

        using var response = await _http
            .PostAsync(_options.RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);

        // すでに無効なトークンでも 400 が返る。切りたいだけなので受け流す
        _ = response;
    }

    /// <summary>認可画面の URL を組み立てる。</summary>
    internal string BuildAuthorizationUrl(string redirectUri, string challenge, string state)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);

        query["client_id"] = _options.ClientId;
        query["redirect_uri"] = redirectUri;
        query["response_type"] = "code";
        query["scope"] = string.Join(' ', _options.Scopes);
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = PkceCodes.Method;
        query["state"] = state;

        // 更新トークンを得るには両方要る。prompt を省くと2回目以降に返らない
        query["access_type"] = "offline";
        query["prompt"] = "consent";

        return $"{_options.AuthorizationEndpoint}?{query}";
    }

    /// <summary>空きポートで待ち受け、戻り先の URL を返す。</summary>
    private static string StartListener(HttpListener listener)
    {
        var port = FreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        listener.Prefixes.Add(redirectUri);
        listener.Start();

        return redirectUri;
    }

    /// <summary>空いているポートを OS に選ばせる。</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>ブラウザが戻ってくるのを待つ。</summary>
    private static async Task<string> WaitForCodeAsync(
        HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(listener.Stop);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
        var error = query["error"];
        var code = query["code"];
        var state = query["state"];

        var message = error is not null
            ? "連携を中止しました。このウィンドウは閉じてかまいません。"
            : "連携が完了しました。このウィンドウは閉じてかまいません。";

        await RespondAsync(context, message).ConfigureAwait(false);

        if (error is not null) throw new OAuthException($"認可されませんでした（{error}）。", error);

        // state が違うなら、こちらが始めた認可ではない
        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new OAuthException("認可の応答が一致しませんでした。もう一度お試しください。");
        }

        if (string.IsNullOrEmpty(code)) throw new OAuthException("認可コードを受け取れませんでした。");

        return code;
    }

    /// <summary>ブラウザに出す短い案内。</summary>
    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = $"""
            <!doctype html><html lang="ja"><head><meta charset="utf-8">
            <title>SlideinaCalendar</title></head>
            <body style="font-family:'Yu Gothic UI',sans-serif;padding:40px;color:#1c2029">
            <p>{WebUtility.HtmlEncode(message)}</p></body></html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>トークンの交換と取り直しで共通の部分。</summary>
    private async Task<OAuthTokens> ExchangeAsync(
        Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        fields["client_id"] = _options.ClientId;
        fields["client_secret"] = _options.ClientSecret;

        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http
            .PostAsync(_options.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException($"トークンを取得できませんでした。{Describe(body)}", ErrorOf(body));
        }

        return Parse(body, _time.GetUtcNow());
    }

    /// <summary>応答を読む。</summary>
    internal static OAuthTokens Parse(string json, DateTimeOffset? now = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (string.IsNullOrEmpty(accessToken)) throw new OAuthException("応答にアクセストークンがありません。");

        var seconds = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;

        return new OAuthTokens
        {
            AccessToken = accessToken,
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            ExpiresAt = (now ?? DateTimeOffset.UtcNow).AddSeconds(seconds),
            Scopes = root.TryGetProperty("scope", out var scope) && scope.GetString() is { } granted
                ? granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [],
        };
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>応答をそのまま見せない。中身にトークンが混ざることがある。</summary>
    private static string Describe(string body) =>
        ErrorOf(body) is { } error ? $"（{error}）" : string.Empty;
}
