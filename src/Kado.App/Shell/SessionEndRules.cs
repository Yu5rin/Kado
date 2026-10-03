namespace Kado.App.Shell;

/// <summary>
/// シャットダウン・サインアウトの合図の扱い。WPF にも Win32 にも触れない純粋な部分で、Linux で試験できる。
/// <para>
/// <b>WPF は、<c>WM_QUERYENDSESSION</c>（終わってよいかの問い合わせ）に「取り消さない」と答えた
/// 時点で、自分で <c>Application.Shutdown()</c> を呼ぶ</b>（<c>Application.WmQueryEndSession</c>）。
/// 実際に終わるのは、全アプリが了承したあとに届く <c>WM_ENDSESSION</c>（wParam=TRUE）のとき。
/// 他のアプリが取り消すと <c>WM_ENDSESSION</c>（wParam=FALSE）が来るが、Kado はもう終わっている。
/// そこで、窓とは別のスレッドで合図だけを見張り、取り消されたら立ち上げ直して常駐を続ける。
/// </para>
/// </summary>
internal static class SessionEndRules
{
    /// <summary>
    /// 問い合わせのあと、結果（<c>WM_ENDSESSION</c>）を待つ上限。
    /// <para>他のアプリの「保存しますか？」で止まっているあいだは待つ。無期限には待たない。</para>
    /// </summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>結果が届いたときの振る舞い。</summary>
    internal enum Reaction
    {
        /// <summary>何もしない（問い合わせを受けていないのに取り消しが来た、など）。</summary>
        Ignore,

        /// <summary>本当に終わる。残っている後片付け（AppBar を外す）を、返る前に済ませる。</summary>
        FinishCleanup,

        /// <summary>取り消された。自分はもう終わりかけなので、立ち上げ直して常駐を続ける。</summary>
        Relaunch,
    }

    /// <param name="sessionIsEnding"><c>WM_ENDSESSION</c> の wParam。TRUE なら本当に終わる。</param>
    /// <param name="queried">終了の問い合わせを受けていて、アプリが自分を終わらせにかかっているか。</param>
    internal static Reaction OnEndSession(bool sessionIsEnding, bool queried)
    {
        // 問い合わせなしに来る（強制的な終了）。後片付けだけは返る前に済ませる。
        // 取り消しが問い合わせなしに来ることは無いので、無視する
        if (!queried) return sessionIsEnding ? Reaction.FinishCleanup : Reaction.Ignore;

        return sessionIsEnding ? Reaction.FinishCleanup : Reaction.Relaunch;
    }
}
