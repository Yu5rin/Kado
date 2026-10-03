using System.Net;
using Kado.Google.OAuth;
using Kado.Google.Sync;

namespace Kado.Presentation.Net;

/// <summary>
/// Google との通信の失敗を種類に分け、利用者に出す文言にする。
/// <para>
/// 以前は「ネットワークに繋がりません」でひとまとめだった。会社の回線では、原因が
/// プロキシの認証（407）・社内の通信検査（証明書）・接続できない・時間切れ・Google が拒否（403）・
/// 呼びすぎ（429）のどれなのかで、次に打つ手がまったく違う。
/// </para>
/// <para>
/// <b>例外の型名は出さない。</b>詳しい連鎖は <see cref="Kado.Core.Net.NetworkDiagnostics.Summarize"/> で
/// shell.log に残す。
/// </para>
/// </summary>
public static class GoogleFailure
{
    /// <summary>Google が理由を付けてこなかったときに入る文字（<c>GoogleHttp.ReadReason</c>）。</summary>
    private const string NoReason = "理由なし";

    /// <summary>
    /// 例外の連鎖から、失敗の種類を決める。
    /// <para>
    /// Google が返した状態（<see cref="GoogleApiException"/>）を最優先にし、次に通信そのものの失敗
    /// （<see cref="NetworkFailure.Classify"/>）を見る。
    /// </para>
    /// </summary>
    public static NetworkFailureKind Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var chain = NetworkFailure.Flatten(ex).ToList();

        if (chain.OfType<GoogleApiException>().FirstOrDefault() is { } api) return ClassifyApi(api);

        // 状態コードを持った通信の例外。GitHub の API の上限と見分けがつかない 403 は、
        // Google では「Google が拒否」と読む
        foreach (var http in chain.OfType<HttpRequestException>())
        {
            if (http.StatusCode == HttpStatusCode.Forbidden) return NetworkFailureKind.Forbidden;
            if (http.StatusCode == HttpStatusCode.Unauthorized) return NetworkFailureKind.Unauthorized;
        }

        return NetworkFailure.Classify(ex);
    }

    /// <summary>Google の API が返した状態から種類を決める。</summary>
    public static NetworkFailureKind ClassifyApi(GoogleApiException api)
    {
        ArgumentNullException.ThrowIfNull(api);

        if (api.IsProxyAuthRequired) return NetworkFailureKind.ProxyAuthRequired;
        if (api.IsRateLimited) return NetworkFailureKind.RateLimited;
        if (api.Status == HttpStatusCode.Forbidden) return NetworkFailureKind.Forbidden;
        if (api.IsUnauthorized) return NetworkFailureKind.Unauthorized;
        if (api.IsMissing) return NetworkFailureKind.NotFound;
        if (api.IsTransient) return NetworkFailureKind.ServerError;

        return NetworkFailureKind.Other;
    }

    /// <summary>
    /// 種類ごとの短い言い方。右上の「同期できません（…）」の括弧の中や、警告の括弧の中に出す。
    /// </summary>
    public static string ShortReason(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.ProxyAuthRequired => "プロキシの認証が必要です（407）",
        NetworkFailureKind.ProxyError => "プロキシ経由の接続に失敗しました",
        NetworkFailureKind.CertificateProblem => "証明書を確認できません（社内の通信検査の可能性）",
        NetworkFailureKind.CannotConnect => "Google に接続できません",
        NetworkFailureKind.Timeout => "時間内に応答がありませんでした",
        NetworkFailureKind.Forbidden => "Google に拒否されました（403）",
        NetworkFailureKind.RateLimited => "呼び出しが多すぎます（429）",
        NetworkFailureKind.ServerError => "Google が一時的に応答できません",
        NetworkFailureKind.Unauthorized => "認証が通りません。繋ぎ直してください",
        NetworkFailureKind.NotFound => "見つかりませんでした（404）",
        NetworkFailureKind.UnreadableResponse or NetworkFailureKind.WebPageInsteadOfFile =>
            "応答を読み取れませんでした（ネットワークの接続先を確認してください）",
        _ => string.Empty,
    };

    /// <summary>
    /// 種類ごとの、次の一手まで添えた言い方。添付の失敗のように、1行で足りない場面に出す。
    /// </summary>
    public static string LongReason(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.ProxyAuthRequired =>
            "会社などのプロキシサーバーが認証を求めていて、通信を通せませんでした（407）。" +
            "ネットワークの管理者にご確認ください。",
        NetworkFailureKind.ProxyError =>
            "プロキシサーバーを経由した接続を確立できませんでした。ネットワークの管理者にご確認ください。",
        NetworkFailureKind.CertificateProblem =>
            "安全な接続（証明書）を確認できませんでした。社内のセキュリティ機器が通信を検査している場合に" +
            "起こることがあります。ネットワークの管理者にご確認ください。",
        NetworkFailureKind.CannotConnect =>
            "Google へ接続できませんでした。インターネットに繋がっているか、" +
            "社内のネットワークで Google への通信が許可されているかをご確認ください。",
        NetworkFailureKind.Timeout =>
            "通信がタイムアウトしました。ネットワークの状態を確かめて、しばらくしてからもう一度お試しください。",
        NetworkFailureKind.Forbidden =>
            "Google に拒否されました（403）。権限が足りないか、この操作が許されていない可能性があります。",
        NetworkFailureKind.RateLimited =>
            "Google への呼び出しが多すぎると断られました（429）。しばらくしてからもう一度お試しください。",
        NetworkFailureKind.ServerError =>
            "Google が一時的に応答できない状態でした。しばらくしてからもう一度お試しください。",
        NetworkFailureKind.Unauthorized =>
            "Google に認証を受け付けてもらえませんでした。接続し直してください。",
        NetworkFailureKind.UnreadableResponse or NetworkFailureKind.WebPageInsteadOfFile =>
            "Google からの応答を読み取れませんでした。会社のネットワークが別のページを返している可能性があります。",
        _ => string.Empty,
    };

    /// <summary>
    /// 例外を、画面に出してよい短い理由にする。種類が決まらなければ <paramref name="fallback"/>。
    /// <para>
    /// Kado 自身が日本語で書いたメッセージ（<see cref="OAuthException"/> など）と、Google が付けてきた
    /// 理由（<see cref="GoogleApiException.Reason"/>）は、そのまま通す。型名と英語の .NET のメッセージは出さない。
    /// </para>
    /// </summary>
    public static string Describe(Exception ex, string fallback)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is OAuthException oauth) return oauth.WasDeclined ? "許可されませんでした" : oauth.Message;

        var kind = Classify(ex);
        var api = NetworkFailure.Flatten(ex).OfType<GoogleApiException>().FirstOrDefault();

        // 見つからない（404）と、種類を決められない 4xx は、Google が付けてきた理由がいちばん手掛かりになる
        if (api is not null && kind is NetworkFailureKind.NotFound or NetworkFailureKind.Other) return api.Reason;

        if (ShortReason(kind) is { Length: > 0 } reason)
        {
            // 403 は、権限が足りない・API が有効でない・容量の上限、のどれなのかを Google の理由が教えてくれる
            return api is not null && kind == NetworkFailureKind.Forbidden && api.Reason != NoReason
                ? $"{reason}: {api.Reason}"
                : reason;
        }

        return api?.Reason ?? fallback;
    }
}
