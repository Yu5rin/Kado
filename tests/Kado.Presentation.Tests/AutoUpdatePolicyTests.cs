using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 自動更新の判断。時刻も画面も持たない純粋な関数なので、実時間を待たずに試せる
/// （操作の無い時間は引数で渡す）。
/// </summary>
public class AutoUpdatePolicyTests
{
    private static readonly Version Current = new(1, 1, 1);

    private static UpdateInfo Info(string tag = "v1.1.2", string? sha = "ABCDEF", bool unavailable = false) =>
        new(
            ReleaseFeed.TryParseVersion(tag, out var v) ? v : new Version(0, 0, 0), tag,
            $"https://github.com/Yu5rin/Kado/releases/download/{tag}/Kado.exe", 1000, sha,
            "https://github.com/Yu5rin/Kado/releases/tag/" + tag, "変更点", unavailable);

    private static StagedUpdate Staged(string tag) =>
        new(tag, "/x/updates/Kado-" + tag + ".exe", "ABCDEF", "", "");

    // ------------------------------------------------------------------
    // 落としてよいか
    // ------------------------------------------------------------------

    [Fact]
    public void 条件がそろえば落とす()
    {
        Assert.Equal(
            AutoDownloadDecision.Yes,
            AutoUpdatePolicy.DecideDownload(true, Info(), Current, null, null, true));
    }

    [Fact]
    public void 設定で切っていれば落とさない()
    {
        Assert.Equal(
            AutoDownloadDecision.Disabled,
            AutoUpdatePolicy.DecideDownload(false, Info(), Current, null, null, true));
    }

    [Fact]
    public void いまの版以下は落とさない()
    {
        Assert.Equal(
            AutoDownloadDecision.NotNewer,
            AutoUpdatePolicy.DecideDownload(true, Info("v1.1.1"), Current, null, null, true));
    }

    [Fact]
    public void ハッシュが無い版は自動では入れ替えない()
    {
        // API が 403 で断られ、配布物の URL を組み立てた経路は、ハッシュが付かず DetailsUnavailable になる
        Assert.Equal(
            AutoDownloadDecision.NoSha256,
            AutoUpdatePolicy.DecideDownload(true, Info(sha: null, unavailable: true), Current, null, null, true));

        Assert.Equal(
            AutoDownloadDecision.NoSha256,
            AutoUpdatePolicy.DecideDownload(true, Info(sha: ""), Current, null, null, true));

        // ハッシュがあっても、詳細を取れていない扱いなら信用しない
        Assert.Equal(
            AutoDownloadDecision.NoSha256,
            AutoUpdatePolicy.DecideDownload(true, Info(unavailable: true), Current, null, null, true));
    }

    [Fact]
    public void 入れ替えに失敗して止めた版は落とさない()
    {
        Assert.Equal(
            AutoDownloadDecision.Blocked,
            AutoUpdatePolicy.DecideDownload(true, Info(), Current, null, "V1.1.2", true));

        // もっと新しい版は止めない
        Assert.Equal(
            AutoDownloadDecision.Yes,
            AutoUpdatePolicy.DecideDownload(true, Info("v1.1.3"), Current, null, "v1.1.2", true));
    }

    [Fact]
    public void もう控えてある版か新しい版は落とさない()
    {
        Assert.Equal(
            AutoDownloadDecision.AlreadyStaged,
            AutoUpdatePolicy.DecideDownload(true, Info(), Current, Staged("v1.1.2"), null, true));

        Assert.Equal(
            AutoDownloadDecision.AlreadyStaged,
            AutoUpdatePolicy.DecideDownload(true, Info(), Current, Staged("v1.2.0"), null, true));

        // 控えより新しい版が出たら落とす
        Assert.Equal(
            AutoDownloadDecision.Yes,
            AutoUpdatePolicy.DecideDownload(true, Info("v1.1.3"), Current, Staged("v1.1.2"), null, true));
    }

    [Fact]
    public void exeのフォルダに書けなければ落とさない()
    {
        Assert.Equal(
            AutoDownloadDecision.CannotWriteInstallDirectory,
            AutoUpdatePolicy.DecideDownload(true, Info(), Current, null, null, false));
    }

    [Fact]
    public void 落とさない理由は全部言葉になる()
    {
        foreach (var decision in Enum.GetValues<AutoDownloadDecision>())
        {
            Assert.NotEqual(decision.ToString(), AutoUpdatePolicy.Describe(decision));
        }

        foreach (var wait in Enum.GetValues<AutoUpdateWait>())
        {
            Assert.NotEqual(wait.ToString(), AutoUpdatePolicy.Describe(wait));
        }
    }

    // ------------------------------------------------------------------
    // 入れ替えてよいか
    // ------------------------------------------------------------------

    /// <summary>手が空いている状態（全部の条件を満たす）。</summary>
    private static AutoUpdateState Idle(TimeSpan? idle = null) => new(
        Enabled: true, HasStaged: true, Busy: false, OtherWindowOpen: false, ModalDialogOpen: false,
        MenuOrPopupOpen: false, DragInProgress: false, SyncRunning: false,
        IdleFor: idle ?? TimeSpan.FromMinutes(10));

    [Fact]
    public void 全部の条件がそろえば入れ替えてよい()
    {
        Assert.True(AutoUpdatePolicy.CanApplyNow(Idle()));
        Assert.Equal(AutoUpdateWait.None, AutoUpdatePolicy.Wait(Idle()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9 * 60 + 59)]
    public void 操作が10分に届かなければ待つ(int seconds)
    {
        var state = Idle(TimeSpan.FromSeconds(seconds));

        Assert.False(AutoUpdatePolicy.CanApplyNow(state));
        Assert.Equal(AutoUpdateWait.NotIdleLongEnough, AutoUpdatePolicy.Wait(state));
    }

    [Fact]
    public void ちょうど10分なら入れ替えてよい()
    {
        Assert.True(AutoUpdatePolicy.CanApplyNow(Idle(AutoUpdatePolicy.RequiredIdle)));
    }

    [Fact]
    public void 操作の無い時間が分からなければ待つ()
    {
        var state = Idle() with { IdleFor = null };

        Assert.False(AutoUpdatePolicy.CanApplyNow(state));
        Assert.Equal(AutoUpdateWait.IdleUnknown, AutoUpdatePolicy.Wait(state));
    }

    [Fact]
    public void 長く放っておいても条件のどれかが欠ければ待つ()
    {
        var long_ = TimeSpan.FromHours(5);

        Assert.Equal(AutoUpdateWait.Disabled, AutoUpdatePolicy.Wait(Idle(long_) with { Enabled = false }));
        Assert.Equal(AutoUpdateWait.NothingStaged, AutoUpdatePolicy.Wait(Idle(long_) with { HasStaged = false }));
        Assert.Equal(AutoUpdateWait.Busy, AutoUpdatePolicy.Wait(Idle(long_) with { Busy = true }));
        Assert.Equal(AutoUpdateWait.OtherWindowOpen, AutoUpdatePolicy.Wait(Idle(long_) with { OtherWindowOpen = true }));
        Assert.Equal(AutoUpdateWait.ModalDialogOpen, AutoUpdatePolicy.Wait(Idle(long_) with { ModalDialogOpen = true }));
        Assert.Equal(AutoUpdateWait.MenuOrDragActive, AutoUpdatePolicy.Wait(Idle(long_) with { MenuOrPopupOpen = true }));
        Assert.Equal(AutoUpdateWait.MenuOrDragActive, AutoUpdatePolicy.Wait(Idle(long_) with { DragInProgress = true }));
        Assert.Equal(AutoUpdateWait.SyncRunning, AutoUpdatePolicy.Wait(Idle(long_) with { SyncRunning = true }));
    }

    [Fact]
    public void 見回りは1分おき()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), AutoUpdatePolicy.PatrolInterval);
    }
}
