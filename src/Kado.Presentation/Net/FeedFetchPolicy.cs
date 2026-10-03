using System.Net;
using System.Net.Http;

namespace Kado.Presentation.Net;

/// <summary>
/// 実働日の配信の自動取得が失敗したときの、やり直し方。
/// <para>
/// <b>一時的な失敗は、印を書かずに、間隔を延ばしながらその日のうちにやり直す。</b>
/// スリープから戻った直後はネットワークが無く、接続できない・時間切れになる。そこで「今日は取りに行った」
/// 印を書くと、その日は二度と取りに行かない。逆に印を書かないまま1分ごとに叩き続けるのも良くない。
/// 5分・15分・1時間と間隔を延ばし、1時間のまま（その日の終わりまで）続ける。
/// </para>
/// <para>
/// <b>恒久的な失敗は、印を書いて翌日に回す。</b>404（置き場が無い）や、中身が読めない
/// （壊れた JSON）は、待っても直らない。
/// </para>
/// </summary>
public static class FeedFetchPolicy
{
    /// <summary>やり直しの間隔。失敗の回数に応じて延ばし、最後のもので止める。</summary>
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// 失敗が <paramref name="failures"/> 回目のあと、次に取りに行くまでの間隔。
    /// </summary>
    /// <param name="failures">これまでに失敗した回数（1以上）。</param>
    public static TimeSpan RetryDelay(int failures) =>
        RetryDelays[Math.Clamp(failures - 1, 0, RetryDelays.Length - 1)];

    /// <summary>
    /// 一時的な失敗か。<c>true</c> なら印を書かずにやり直す。
    /// <list type="bullet">
    /// <item>接続できない・名前が引けない・時間切れ・途中で切れた</item>
    /// <item>プロキシ・証明書の一時的な不調（VPN がまだ繋がっていない、など）</item>
    /// <item>5xx、429、408、407</item>
    /// </list>
    /// 404 などの4xx と、中身が読めない（壊れた JSON）や設定の誤りは、恒久的とみなす。
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var chain = NetworkFailure.Flatten(ex).ToList();

        // 状態コードがあれば、それがいちばん確かな手掛かり
        foreach (var http in chain.OfType<HttpRequestException>())
        {
            if (http.StatusCode is { } code) return IsTransientStatus(code);
        }

        switch (NetworkFailure.Classify(ex))
        {
            case NetworkFailureKind.CannotConnect:
            case NetworkFailureKind.Timeout:
            case NetworkFailureKind.ProxyError:
            case NetworkFailureKind.ProxyAuthRequired:
            case NetworkFailureKind.CertificateProblem:
            case NetworkFailureKind.ServerError:
                return true;

            case NetworkFailureKind.UnreadableResponse:
            case NetworkFailureKind.WebPageInsteadOfFile:
            case NetworkFailureKind.NotFound:
                return false;
        }

        // 種類が決まらない通信の例外（接続が途中で切れた、など）は、通信の不調とみなす。
        // 通信とは関係のない例外（設定の誤りなど）は、待っても直らない
        return chain.OfType<HttpRequestException>().Any();
    }

    /// <summary>状態コードのうち、待てば直る見込みがあるもの。</summary>
    public static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ProxyAuthenticationRequired
        || (int)status is >= 500 and <= 599;

    /// <summary>
    /// 手で押した取得が失敗したときの、画面に出す短い理由。<b>型名と英語の .NET のメッセージは出さない</b>。
    /// 自動の取得は画面に断りを出さないので、これを使うのは手で押したときだけ。
    /// </summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var chain = NetworkFailure.Flatten(ex).ToList();

        if (chain.OfType<HttpRequestException>().Any(h => h.StatusCode == HttpStatusCode.Forbidden))
        {
            return "配信元に拒否されました（403）";
        }

        switch (NetworkFailure.Classify(ex))
        {
            case NetworkFailureKind.ProxyAuthRequired: return "プロキシの認証が必要です（407）";
            case NetworkFailureKind.ProxyError: return "プロキシ経由の接続に失敗しました";
            case NetworkFailureKind.CertificateProblem: return "証明書を確認できません（社内の通信検査の可能性）";
            case NetworkFailureKind.CannotConnect: return "配信元へ接続できません";
            case NetworkFailureKind.Timeout: return "時間内に応答がありませんでした";
            case NetworkFailureKind.NotFound: return "配信元にファイルが見つかりません（404）";
            case NetworkFailureKind.ServerError: return "配信元が一時的に応答できません";
            case NetworkFailureKind.RateLimited: return "配信元への問い合わせが多すぎます（429）";
            case NetworkFailureKind.WebPageInsteadOfFile: return "ファイルではなく Web ページが返りました";
        }

        // Kado が日本語で書いた文（大きすぎる・実働日データとして読めない、など）はそのまま通す
        return ex.Message.Any(c => c is >= '\u3040' and <= '\u30FF') ? ex.Message : "取り込めませんでした";
    }
}
