namespace Kado.Google.Sync;

/// <summary>1件について、同期で何をするか。</summary>
public enum SyncAction
{
    /// <summary>何もしない。どちらも変わっていない。</summary>
    None,

    /// <summary>相手の内容でこちらを上書きする。</summary>
    Pull,

    /// <summary>こちらの内容を相手へ送る。</summary>
    Push,

    /// <summary>こちらで消す。相手が消したため。</summary>
    DeleteLocal,

    /// <summary>相手で消す。こちらで消したため。</summary>
    DeleteRemote,

    /// <summary>新しく作る。相手にしか無い。</summary>
    CreateLocal,

    /// <summary>相手に作る。こちらにしか無い。</summary>
    CreateRemote,
}

/// <summary>
/// 1件ぶんの判断と、その理由。
/// <para>理由を持たせているのは、同期の記録を読んで追えるようにするため。</para>
/// </summary>
/// <param name="Action">何をするか。</param>
/// <param name="Reason">なぜそうするか。画面と記録に出す。</param>
public readonly record struct SyncDecision(SyncAction Action, string Reason)
{
    /// <summary>何もしない。</summary>
    public static SyncDecision Nothing => new(SyncAction.None, "変わっていない");

    /// <summary>書き込みを伴うか。件数を数えるときに使う。</summary>
    public bool Changes => Action != SyncAction.None;
}

/// <summary>
/// どちらを採るかの決め方。
/// <para>
/// <b>どちらも変わっていたときは、相手（Google）を採る。</b>予定は複数の端末や
/// 同僚から書き換わるもので、そちらが正であることが多い。こちらの端末で
/// 開いたまま放置した内容が、他の人の変更を押し戻すほうが困る。
/// </para>
/// <para>
/// ただし<b>消したほうが勝つ</b>。消したのに戻ってくるのは、変更が消えるより
/// 分かりにくい。片方が消していれば、もう片方が変わっていても消す。
/// </para>
/// </summary>
public static class SyncPolicy
{
    /// <summary>
    /// 1件ぶんの判断。
    /// </summary>
    /// <param name="hasLocal">こちらに残っているか。</param>
    /// <param name="hasRemote">相手に残っているか。</param>
    /// <param name="locallyDeleted">こちらで消した記録があるか。</param>
    /// <param name="remoteDeleted">相手が消したと伝えてきたか。</param>
    /// <param name="localChanged">こちらで変わったか（相手へ未送信の変更があるか）。</param>
    /// <param name="remoteChanged">相手で変わったか。</param>
    /// <param name="linked">相手側の識別子で結び付いているか。</param>
    public static SyncDecision Decide(
        bool hasLocal,
        bool hasRemote,
        bool locallyDeleted = false,
        bool remoteDeleted = false,
        bool localChanged = false,
        bool remoteChanged = false,
        bool linked = false)
    {
        // 消したほうが勝つ。戻ってくるほうが分かりにくい
        if (remoteDeleted)
        {
            return hasLocal
                ? new SyncDecision(SyncAction.DeleteLocal, "相手が削除した")
                : SyncDecision.Nothing;
        }

        if (locallyDeleted)
        {
            return hasRemote
                ? new SyncDecision(SyncAction.DeleteRemote, "こちらで削除した")
                : SyncDecision.Nothing;
        }

        if (!hasLocal && !hasRemote) return SyncDecision.Nothing;

        // 片方にしか無い。結び付いていなければ、まだ相手に作っていないだけ
        if (!hasLocal) return new SyncDecision(SyncAction.CreateLocal, "相手にだけある");

        if (!hasRemote)
        {
            // 結び付いていたのに相手から消えた＝相手側で消された
            return linked
                ? new SyncDecision(SyncAction.DeleteLocal, "相手から消えた")
                : new SyncDecision(SyncAction.CreateRemote, "こちらにだけある");
        }

        // どちらも変わった。相手を採る
        if (localChanged && remoteChanged)
        {
            return new SyncDecision(SyncAction.Pull, "どちらも変わったので相手を採る");
        }

        if (remoteChanged) return new SyncDecision(SyncAction.Pull, "相手が変わった");
        if (localChanged) return new SyncDecision(SyncAction.Push, "こちらが変わった");

        return SyncDecision.Nothing;
    }
}
