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
    GoogleOAuthOptions options, HttpClient http, Action<string> openBrowser,
    TimeProvider? time = null, TimeSpan? authorizationTimeout = null)
{
    private readonly GoogleOAuthOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Action<string> _openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));

    /// <summary>失効時刻の起点。トークンを配る側と同じ時計でないと食い違う。</summary>
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// ブラウザでの許可待ちの上限。
    /// <para>タブを閉じたまま放置されると <c>GetContextAsync</c> が無期限に待つので、ここで諦める。</para>
    /// </summary>
    private readonly TimeSpan _authorizationTimeout = authorizationTimeout ?? TimeSpan.FromMinutes(5);

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

        // 呼び出し側の取り消しと、こちらの上限とをまとめて1本の待ちにする
        using var timeoutSource = new CancellationTokenSource(_authorizationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var code = await WaitForCodeAsync(listener, state, linked.Token, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// ブラウザが戻ってくるのを待つ。
    /// <para>
    /// <paramref name="waitToken"/> は「呼び出し側の取り消し」と「こちらの上限（5分）」を
    /// まとめたもの。<c>HttpListener</c> は <see cref="CancellationToken"/> を直接受けないので、
    /// <c>Register</c> で <c>Stop()</c> を呼んで待ちを解く。どちらが理由で解けたのかは
    /// <paramref name="callerToken"/> だけを見て判断する（こちらは上限では動かない）。
    /// </para>
    /// <para>
    /// <b>関係ないリクエストは 404 を返して待ち続ける。</b>ブラウザが認可画面を開く前に
    /// <c>/favicon.ico</c> を先取りしにいくことがあり、path や state が合わない最初の
    /// リクエストをそのまま受け取って終わらせると、あとから届く本来の認可コードを
    /// 逃してしまう。待ち続ける時間そのものは <paramref name="waitToken"/> の5分上限で
    /// 変わらない。
    /// </para>
    /// </summary>
    private static async Task<string> WaitForCodeAsync(
        HttpListener listener, string expectedState, CancellationToken waitToken, CancellationToken callerToken)
    {
        using var registration = waitToken.Register(listener.Stop);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (waitToken.IsCancellationRequested &&
                                        ex is HttpListenerException or ObjectDisposedException)
            {
                // 呼び出し側が止めたのか、5分待って諦めたのかで、出す文言を変える
                if (callerToken.IsCancellationRequested) throw new OperationCanceledException(callerToken);

                throw new OAuthException("ブラウザで許可されませんでした。もう一度お試しください。");
            }

            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            var error = query["error"];
            var code = query["code"];
            var state = query["state"];

            if (!IsExpectedCallback(context.Request, expectedState, error, code, state))
            {
                await RespondNotFoundAsync(context).ConfigureAwait(false);
                continue;
            }

            var message = error is not null
                ? "連携を中止しました。このウィンドウは閉じてかまいません。"
                : "連携が完了しました。このウィンドウは閉じてかまいません。";

            await RespondAsync(context, message).ConfigureAwait(false);

            if (error is not null) throw new OAuthException($"認可されませんでした（{error}）。", error);
            if (string.IsNullOrEmpty(code)) throw new OAuthException("認可コードを受け取れませんでした。");

            return code;
        }
    }

    /// <summary>
    /// 待っている認可コードの応答か。
    /// <para>
    /// path が登録したリダイレクト先（<c>/</c>）でない、または <c>state</c> が
    /// こちらが送ったものと違うなら、無関係なリクエストとして扱う。
    /// </para>
    /// </summary>
    private static bool IsExpectedCallback(
        HttpListenerRequest request, string expectedState, string? error, string? code, string? state)
    {
        if (request.Url is null || request.Url.AbsolutePath != "/") return false;

        // error も code も無いなら、認可の応答そのものではない
        if (error is null && code is null) return false;

        return string.Equals(state, expectedState, StringComparison.Ordinal);
    }

    /// <summary>無関係なリクエストには 404 だけ返して切る。認可の待ちは続ける。</summary>
    private static async Task RespondNotFoundAsync(HttpListenerContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;

        await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
        context.Response.Close();
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
