using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// どちらを採るかの決め方。
/// <para>
/// 통信を含まない純粋な判断なので、組み合わせを網羅して確かめられる。
/// </para>
/// </summary>
public class SyncPolicyTests
{
    [Fact]
    public void どちらも変わっていなければ何もしない()
    {
        var decision = SyncPolicy.Decide(hasLocal: true, hasRemote: true, linked: true);

        Assert.Equal(SyncAction.None, decision.Action);
        Assert.False(decision.Changes);
    }

    [Fact]
    public void 相手が変わったら取り込む()
    {
        var decision = SyncPolicy.Decide(
            hasLocal: true, hasRemote: true, remoteChanged: true, linked: true);

        Assert.Equal(SyncAction.Pull, decision.Action);
    }

    [Fact]
    public void こちらが変わったら送る()
    {
        var decision = SyncPolicy.Decide(
            hasLocal: true, hasRemote: true, localChanged: true, linked: true);

        Assert.Equal(SyncAction.Push, decision.Action);
    }

    [Fact]
    public void どちらも変わったら相手を採る()
    {
        var decision = SyncPolicy.Decide(
            hasLocal: true, hasRemote: true,
            localChanged: true, remoteChanged: true, linked: true);

        // 予定は複数の端末や同僚から書き換わる。こちらで放置した内容で
        // 他の人の変更を押し戻すほうが困る
        Assert.Equal(SyncAction.Pull, decision.Action);
        Assert.Equal("どちらも変わったので相手を採る", decision.Reason);
    }

    [Fact]
    public void 相手が消したらこちらも消す()
    {
        var decision = SyncPolicy.Decide(hasLocal: true, hasRemote: false, remoteDeleted: true);

        Assert.Equal(SyncAction.DeleteLocal, decision.Action);
    }

    [Fact]
    public void こちらが消したら相手も消す()
    {
        var decision = SyncPolicy.Decide(hasLocal: false, hasRemote: true, locallyDeleted: true);

        Assert.Equal(SyncAction.DeleteRemote, decision.Action);
    }

    [Fact]
    public void 消したほうが変更に勝つ()
    {
        // 消したのに戻ってくるのは、変更が消えるより分かりにくい
        var remoteWins = SyncPolicy.Decide(
            hasLocal: true, hasRemote: false,
            remoteDeleted: true, localChanged: true, linked: true);

        Assert.Equal(SyncAction.DeleteLocal, remoteWins.Action);

        var localWins = SyncPolicy.Decide(
            hasLocal: false, hasRemote: true,
            locallyDeleted: true, remoteChanged: true, linked: true);

        Assert.Equal(SyncAction.DeleteRemote, localWins.Action);
    }

    [Fact]
    public void 両方で消えていれば何もしない()
    {
        var decision = SyncPolicy.Decide(
            hasLocal: false, hasRemote: false, locallyDeleted: true, remoteDeleted: true);

        Assert.Equal(SyncAction.None, decision.Action);
    }

    [Fact]
    public void 相手にだけあれば作る()
    {
        var decision = SyncPolicy.Decide(hasLocal: false, hasRemote: true);

        Assert.Equal(SyncAction.CreateLocal, decision.Action);
    }

    [Fact]
    public void こちらにだけあってまだ結び付いていなければ相手に作る()
    {
        var decision = SyncPolicy.Decide(hasLocal: true, hasRemote: false, linked: false);

        Assert.Equal(SyncAction.CreateRemote, decision.Action);
    }

    [Fact]
    public void 結び付いていたのに相手から消えたら削除とみなす()
    {
        // 一度は相手にあった。それが一覧から消えたなら、相手側で消された
        var decision = SyncPolicy.Decide(hasLocal: true, hasRemote: false, linked: true);

        Assert.Equal(SyncAction.DeleteLocal, decision.Action);
        Assert.Equal("相手から消えた", decision.Reason);
    }

    [Fact]
    public void どちらにも無ければ何もしない()
    {
        Assert.Equal(SyncAction.None, SyncPolicy.Decide(hasLocal: false, hasRemote: false).Action);
    }
}
