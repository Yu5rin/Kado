using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;
using Kado.Core.Net;

namespace Kado.Google.OAuth;

/// <summary>認可に失敗した。利用者が断った場合も含む。</summary>
public sealed class OAuthException(string message, string? error = null, Exception? innerException = null)
    : Exception(message, innerException)
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
    TimeProvider? time = null, TimeSpan? authorizationTimeout = null, Action<string>? log = null)
{
    private readonly GoogleOAuthOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Action<string> _openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));

    /// <summary>失効時刻の起点。トークンを配る側と同じ時計でないと食い違う。</summary>
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// 失敗した通信の記録（shell.log）。応答の要点（状態・Content-Type・経由）と例外の連鎖を、1行で渡す。
    /// <b>トークン・認可コード・本文は書かない。</b>
    /// </summary>
    private readonly Action<string>? _log = log;

    /// <summary>
    /// ブラウザでの許可待ちの上限。
    /// <para>タブを閉じたまま放置されると <c>GetContextAsync</c> が無期限に待つので、ここで諦める。</para>
    /// </summary>
    private readonly TimeSpan _authorizationTimeout = authorizationTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>
    /// 認可を受けてトークンを取る。
    /// <para>ブラウザが閉じられたままだと戻らないので、呼び出し側で打ち切れるようにしておく。</para>
    /// </summary>
    public Task<OAuthTokens> AuthorizeAsync(CancellationToken cancellationToken = default) =>
        AuthorizeAsync(_options.Scopes, includeGrantedScopes: false, cancellationToken);

    /// <summary>
    /// 権限を追加で認可する。
    /// <para>
    /// <paramref name="scopes"/> には、足りない分だけを渡す（例：添付のための
    /// <c>drive.file</c> の1つだけ）。<paramref name="includeGrantedScopes"/> を true にすると、
    /// これまでに許可済みの権限も維持したまま返ってくる。false で呼ぶと、通常の
    /// <see cref="AuthorizeAsync(CancellationToken)"/> と同じ動きになる。
    /// </para>
    /// </summary>
    public async Task<OAuthTokens> AuthorizeAsync(
        IReadOnlyList<string> scopes, bool includeGrantedScopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var pkce = PkceCodes.Create();
        var state = PkceCodes.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        var redirectUri = StartListener(listener, _log);

        _openBrowser(BuildAuthorizationUrl(redirectUri, pkce.Challenge, state, scopes, includeGrantedScopes));

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

        try
        {
            using var response = await _http
                .PostAsync(_options.RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);

            // すでに無効なトークンでも 400 が返る。切りたいだけなので受け流す。
            // ただし 407 や 5xx など、取り消しが届いていない応答は記録に残す
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            {
                Write(_log, $"取り消し: {NetworkDiagnostics.DescribeResponse(response)}");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Write(_log, $"取り消し: 通信に失敗。{NetworkDiagnostics.Summarize(ex)}");
            throw;
        }
    }

    /// <summary>認可画面の URL を組み立てる。</summary>
    internal string BuildAuthorizationUrl(string redirectUri, string challenge, string state) =>
        BuildAuthorizationUrl(redirectUri, challenge, state, _options.Scopes, includeGrantedScopes: false);

    /// <summary>
    /// 認可画面の URL を組み立てる（権限を指定できる版）。
    /// <para>
    /// <paramref name="includeGrantedScopes"/> は追加認可のときだけ true にする。
    /// 通常の接続では false（省略時は Google 側の既定＝false と同じ）のままでよい。
    /// </para>
    /// </summary>
    internal string BuildAuthorizationUrl(
        string redirectUri, string challenge, string state,
        IReadOnlyList<string> scopes, bool includeGrantedScopes)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);

        query["client_id"] = _options.ClientId;
        query["redirect_uri"] = redirectUri;
        query["response_type"] = "code";
        query["scope"] = string.Join(' ', scopes);
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = PkceCodes.Method;
        query["state"] = state;

        // 更新トークンを得るには両方要る。prompt を省くと2回目以降に返らない
        query["access_type"] = "offline";
        query["prompt"] = "consent";

        if (includeGrantedScopes) query["include_granted_scopes"] = "true";

        return $"{_options.AuthorizationEndpoint}?{query}";
    }

    /// <summary>
    /// 空きポートで待ち受け、戻り先の URL を返す。
    /// <para>
    /// 開始できないことがある（URL の予約が無く権限が足りない＝Access denied、ポートを別のアプリに
    /// 取られた、セキュリティ ソフトに止められた、など）。裸の <see cref="HttpListenerException"/> のまま
    /// 漏らすと、画面は「接続できませんでした」としか言えない。理由つきの <see cref="OAuthException"/> に
    /// して、原因（Win32 のエラー番号）を記録に残す。
    /// </para>
    /// </summary>
    private static string StartListener(HttpListener listener, Action<string>? log)
    {
        var port = FreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        try
        {
            listener.Prefixes.Add(redirectUri);
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw ToOAuthException(ex, log);
        }

        return redirectUri;
    }

    /// <summary>
    /// 受け口を開始できなかったことを、理由つきの <see cref="OAuthException"/> にする。原因は記録に残す。
    /// </summary>
    internal static OAuthException ToOAuthException(HttpListenerException ex, Action<string>? log)
    {
        Write(log, $"認可の受け口を開始できなかった（{DescribeListenerFailure(ex)}）。" +
                   $"{NetworkDiagnostics.Summarize(ex)}");

        return new OAuthException(
            "ブラウザからの戻りを受け取る準備ができませんでした" +
            $"（{DescribeListenerFailure(ex)}）。セキュリティ ソフトなどが 127.0.0.1 の待ち受けを止めていないか、" +
            "確かめてください。", innerException: ex);
    }

    /// <summary>
    /// 受け口を開始できなかった理由を、Win32 のエラー番号から言葉にする。
    /// 5 は権限（Access denied）、32・183 は別のアプリが使用中。
    /// </summary>
    internal static string DescribeListenerFailure(HttpListenerException ex) => ex.ErrorCode switch
    {
        5 => "権限が足りません（エラー 5）",
        32 or 183 => $"別のアプリが使っています（エラー {ex.ErrorCode}）",
        var code => $"エラー {code}",
    };

    private static void Write(Action<string>? log, string message)
    {
        try
        {
            log?.Invoke(message);
        }
        catch (Exception)
        {
            // 記録できなくても、認可そのものは続ける
        }
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
            <title>Kado</title></head>
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

        var label = fields.TryGetValue("grant_type", out var grant) && grant == "refresh_token"
            ? "トークンの取り直し"
            : "トークンの交換";

        using var content = new FormUrlEncodedContent(fields);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .PostAsync(_options.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 通信そのものが転んだ（プロキシ・証明書・接続・時間切れ）。原因は例外の連鎖にある
            Write(_log, $"{label}: 通信に失敗。{NetworkDiagnostics.Summarize(ex)}");
            throw;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorOf(body);

                Write(_log, $"{label}: 失敗。{NetworkDiagnostics.DescribeResponse(response)}" +
                            (error is null ? string.Empty : $", error={error}"));

                // Google の言葉（error）が付いていれば、それを伝える。付いていなければ、Google ではなく
                // 途中の中継（プロキシの 407・エラーページ）や障害が返したもの。状態コードごと投げて、
                // 画面が「プロキシの認証」「時間切れ」などを言い分けられるようにする
                if (error is null)
                {
                    throw new HttpRequestException(
                        "成功以外の状態コードが返りました", null, response.StatusCode);
                }

                throw new OAuthException($"トークンを取得できませんでした。{Describe(body)}", error);
            }

            return Parse(body, _time.GetUtcNow());
        }
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
