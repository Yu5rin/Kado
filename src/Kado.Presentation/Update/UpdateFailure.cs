using System.Net;
using Kado.Presentation.Net;

namespace Kado.Presentation.Update;

/// <summary>更新を確かめられなかった理由。画面に出す文言とリリースのページを持つ。</summary>
/// <param name="Kind">理由の種類。</param>
/// <param name="Message">利用者に見せる文言。例外の型名は含まない。</param>
/// <param name="ReleasePageUrl">手で確かめるときのリリースのページ。組み立てられなければ空。</param>
public sealed record UpdateFailure(NetworkFailureKind Kind, string Message, string ReleasePageUrl)
{
    /// <summary>「更新を確かめられませんでした。」に理由を添えた文言にする。</summary>
    public static UpdateFailure From(Exception ex, string releasePageUrl)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return new UpdateFailure(Classify(ex), CheckMessage(ex), releasePageUrl);
    }

    /// <summary>確認が失敗したときの文言。</summary>
    public static string CheckMessage(Exception ex) =>
        "更新を確かめられませんでした。" + Reason(ex);

    /// <summary>ダウンロードや入れ替えが失敗したときの文言。手で差し替える道も添える。</summary>
    public static string DownloadMessage(Exception ex)
    {
        var reason = Reason(ex);

        return reason.Contains("リリースページ", StringComparison.Ordinal)
            ? "更新できませんでした。" + reason
            : "更新できませんでした。" + reason + "リリースのページから手で差し替えてください。";
    }

    /// <summary>
    /// 例外から、画面に出してよい理由の文を作る。<b>型名は出さない。</b>
    /// <para>
    /// Kado 自身が日本語で書いたメッセージ（ハッシュが合わない、など）は、そのまま通す。
    /// .NET が返す英語のメッセージはパスや状態コードを含むことがあり、利用者には手掛かりに
    /// ならないので、種類ごとの言い換えにする。詳細は shell.log に残る。
    /// </para>
    /// </summary>
    public static string Reason(Exception ex)
    {
        var kind = Classify(ex);

        if (kind == NetworkFailureKind.Other && ContainsKana(ex.Message)) return ex.Message;

        return Reason(kind);
    }

    /// <summary>種類ごとの、利用者向けの理由の文。</summary>
    public static string Reason(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.RateLimited =>
            "配布元への問い合わせが回数の上限に達していました。同じネットワークを使う人たちで共有される上限なので、" +
            "自分が何度も押していなくても起こります。しばらく時間をおくか、リリースページから直接ご確認ください。",
        NetworkFailureKind.ProxyAuthRequired =>
            "会社などのプロキシサーバーが認証を求めていて、通信を通せませんでした（407）。" +
            "Kado は Windows にログオン中の資格情報で認証を試みましたが、通りませんでした。" +
            "ネットワークの管理者にご確認いただくか、リリースページから直接ダウンロードしてください。",
        NetworkFailureKind.ProxyError =>
            "プロキシサーバーを経由した接続を確立できませんでした。" +
            "ネットワークの管理者にご確認いただくか、リリースページから直接ダウンロードしてください。",
        NetworkFailureKind.Timeout =>
            "配布元から時間内に応答がありませんでした。ネットワークの状態を確認して、しばらくしてからもう一度お試しください。",
        NetworkFailureKind.CannotConnect =>
            "配布元（GitHub）へ接続できませんでした。インターネットに繋がっているか、" +
            "社内のネットワークで GitHub への通信が許可されているかをご確認ください。",
        NetworkFailureKind.CertificateProblem =>
            "安全な接続（証明書）を確認できませんでした。社内のセキュリティ機器が通信を検査している場合に起こることがあります。" +
            "ネットワークの管理者にご確認ください。",
        NetworkFailureKind.NotFound =>
            "配布元でリリースが見つかりませんでした。リリースのページをご確認ください。",
        NetworkFailureKind.ServerError =>
            "配布元が一時的に応答できない状態でした。しばらくしてからもう一度お試しください。",
        NetworkFailureKind.UnreadableResponse =>
            "配布元からの応答を読み取れませんでした。ネットワークの途中で応答が差し替えられている可能性があります。",
        NetworkFailureKind.WebPageInsteadOfFile =>
            "取りに行ったものではなく、別のもの（Web ページなど）が返りました。ネットワークの途中で" +
            "差し替えられている可能性があります（プロキシのエラーページなど）。" +
            "リリースページから直接ダウンロードしてください。",
        _ => "ネットワークをご確認ください。",
    };

    /// <summary>「通信を確かめる」の一覧に出す短い言い方。</summary>
    public static string ShortReason(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.RateLimited => "問い合わせ回数の上限",
        NetworkFailureKind.ProxyAuthRequired => "プロキシの認証が必要",
        NetworkFailureKind.ProxyError => "プロキシ経由の接続に失敗",
        NetworkFailureKind.Timeout => "時間内に応答なし",
        NetworkFailureKind.CannotConnect => "接続できない",
        NetworkFailureKind.CertificateProblem => "証明書を確認できない",
        NetworkFailureKind.NotFound => "見つからない",
        NetworkFailureKind.ServerError => "配布元が応答できない",
        NetworkFailureKind.UnreadableResponse => "応答を読み取れない",
        NetworkFailureKind.WebPageInsteadOfFile => "Web ページが返った",
        _ => "原因は shell.log を参照",
    };

    /// <inheritdoc cref="NetworkFailure.Classify"/>
    public static NetworkFailureKind Classify(Exception ex) => NetworkFailure.Classify(ex);

    /// <inheritdoc cref="NetworkFailure.ClassifyStatus"/>
    public static NetworkFailureKind? ClassifyStatus(HttpStatusCode status) => NetworkFailure.ClassifyStatus(status);

    /// <inheritdoc cref="NetworkFailure.Flatten"/>
    internal static IEnumerable<Exception> Flatten(Exception ex, int depth = 0) => NetworkFailure.Flatten(ex, depth);

    private static bool ContainsKana(string text)
    {
        foreach (var c in text)
        {
            // ひらがな（U+3040-309F）とカタカナ（U+30A0-30FF）。Kado が日本語で書いたと分かる
            if (c is >= '぀' and <= 'ヿ') return true;
        }

        return false;
    }
}
