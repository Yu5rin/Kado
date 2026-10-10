using Kado.App;
using Kado.App.Shell;

namespace Kado.Shell.Tests;

/// <summary>起動・常駐まわりの、WPF に触れない判断。</summary>
public class ResilienceRulesTests
{
    // ------------------------------------------------------------------
    // 項目11: パイプ名はセッションとユーザーごと
    // ------------------------------------------------------------------

    [Fact]
    public void パイプ名にセッションとSIDが入る()
    {
        var name = ActivationPipe.NameFor(2, "S-1-5-21-111-222-333-1001");

        Assert.Equal("Kado.Activate.2.S-1-5-21-111-222-333-1001", name);
    }

    [Fact]
    public void セッションが違えば名前も違う()
    {
        Assert.NotEqual(
            ActivationPipe.NameFor(1, "S-1-5-21-1"),
            ActivationPipe.NameFor(2, "S-1-5-21-1"));
    }

    [Fact]
    public void ユーザーが違えば名前も違う()
    {
        Assert.NotEqual(
            ActivationPipe.NameFor(1, "S-1-5-21-1"),
            ActivationPipe.NameFor(1, "S-1-5-21-2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SIDが取れなくても名前を作れる(string? sid)
    {
        Assert.Equal("Kado.Activate.3.unknown", ActivationPipe.NameFor(3, sid));
    }

    [Fact]
    public void パイプ名に使えない文字は置き換える()
    {
        var name = ActivationPipe.NameFor(1, @"DOMAIN\user/name");

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 250)]
    [InlineData(2, 500)]
    [InlineData(3, 1000)]
    [InlineData(5, 4000)]
    [InlineData(6, 5000)]
    [InlineData(50, 5000)]
    public void 待ち受けのやり直しは間をおき上限で止まる(int failures, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), ActivationPipe.RetryDelay(failures));
    }

    // ------------------------------------------------------------------
    // 項目5: 立ち上げ直しは前のプロセスの終了を待つ
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("--after-update", true)]
    [InlineData("--after-restart", true)]
    [InlineData("--other", false)]
    public void 立ち上げ直しの引数があれば前のプロセスを待つ(string argument, bool expected)
    {
        Assert.Equal(expected, StartupArguments.WaitsForPreviousProcess([argument]));
    }

    [Fact]
    public void 引数が無ければ待たない()
    {
        Assert.False(StartupArguments.WaitsForPreviousProcess([]));
    }

    [Fact]
    public void 更新の合図は更新側の定数と同じ値()
    {
        // UpdateService.AfterUpdateArgument と食い違うと、更新直後の起動が前のプロセスを待たなくなる
        var update = File.ReadAllText(Path.Combine(AppDirectory(), "Update", "UpdateService.cs"));

        Assert.Contains($"AfterUpdateArgument = \"{StartupArguments.AfterUpdate}\"", update, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--keep-hidden", true)]
    [InlineData("--after-update", false)]
    public void 窓を隠したままの合図を読める(string argument, bool expected)
    {
        Assert.Equal(expected, StartupArguments.KeepsHidden(["--after-update", argument]));
    }

    [Fact]
    public void 窓を隠したままの合図は更新側の定数と同じ値()
    {
        // 食い違うと、トレイに入っていた窓が自動更新のたびに前へ出てくる
        var update = File.ReadAllText(Path.Combine(AppDirectory(), "Update", "UpdateService.cs"));

        Assert.Contains($"KeepHiddenArgument = \"{StartupArguments.KeepHidden}\"", update, StringComparison.Ordinal);
    }

    private static string AppDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Kado.App")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Kado.App");
    }

    // ------------------------------------------------------------------
    // 項目6: トレイ
    // ------------------------------------------------------------------

    [Fact]
    public void トレイのやり直しは間をおいて延ばし60秒で止まる()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), TrayPolicy.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), TrayPolicy.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(15), TrayPolicy.RetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(30), TrayPolicy.RetryDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(60), TrayPolicy.RetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(60), TrayPolicy.RetryDelay(500));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, true, false)]  // トレイに出ていなければ隠さない（隠すと戻れない）
    [InlineData(false, true, false, false)]  // 設定で切っていれば隠さない
    [InlineData(true, true, true, false)]    // 終わると決めているときは隠さない
    public void 閉じるボタンで隠してよいのはトレイに出ているときだけ(
        bool reallyExiting, bool trayShown, bool closeToTray, bool expected)
    {
        Assert.Equal(expected, TrayPolicy.HidesOnClose(reallyExiting, trayShown, closeToTray));
    }

    // ------------------------------------------------------------------
    // 項目10: シャットダウンの取り消し
    // ------------------------------------------------------------------

    [Fact]
    public void 本当に終わるなら後片付けを返る前に済ませる()
    {
        Assert.Equal(SessionEndRules.Reaction.FinishCleanup, SessionEndRules.OnEndSession(true, queried: true));
    }

    [Fact]
    public void 問い合わせなしに終わるときも後片付けをする()
    {
        // 強制的なシャットダウンは、問い合わせなしに終了だけが来る
        Assert.Equal(SessionEndRules.Reaction.FinishCleanup, SessionEndRules.OnEndSession(true, queried: false));
    }

    [Fact]
    public void 他のアプリが取り消したら立ち上げ直す()
    {
        Assert.Equal(SessionEndRules.Reaction.Relaunch, SessionEndRules.OnEndSession(false, queried: true));
    }

    [Fact]
    public void 問い合わせを受けていない取り消しは無視する()
    {
        Assert.Equal(SessionEndRules.Reaction.Ignore, SessionEndRules.OnEndSession(false, queried: false));
    }

    [Fact]
    public void 結果を待つ上限は無期限ではない()
    {
        Assert.InRange(SessionEndRules.MaxWait, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
    }

    // ------------------------------------------------------------------
    // 項目7: 前に出るメッセージ
    // ------------------------------------------------------------------

    // 列挙型は internal なので、テストの引数には int で渡して中で戻す
    [Theory]
    [InlineData((int)FrontButtons.Ok, (int)FrontIcon.Information)]
    [InlineData((int)FrontButtons.YesNo, (int)FrontIcon.Warning)]
    [InlineData((int)FrontButtons.YesNoCancel, (int)FrontIcon.Error)]
    [InlineData((int)FrontButtons.RetryCancel, (int)FrontIcon.Error)]
    public void メッセージは必ず最前面と前景の印を持つ(int buttons, int icon)
    {
        var flags = FrontMessageBox.Flags((FrontButtons)buttons, (FrontIcon)icon);

        Assert.NotEqual(0u, flags & 0x40000u);   // MB_TOPMOST
        Assert.NotEqual(0u, flags & 0x10000u);   // MB_SETFOREGROUND
    }

    [Fact]
    public void ボタンの組み合わせが種別に出る()
    {
        Assert.Equal(0x5u, FrontMessageBox.Flags(FrontButtons.RetryCancel, FrontIcon.Error) & 0xFu);
        Assert.Equal(0x3u, FrontMessageBox.Flags(FrontButtons.YesNoCancel, FrontIcon.Error) & 0xFu);
        Assert.Equal(0x4u, FrontMessageBox.Flags(FrontButtons.YesNo, FrontIcon.Error) & 0xFu);
        Assert.Equal(0x0u, FrontMessageBox.Flags(FrontButtons.Ok, FrontIcon.Error) & 0xFu);
    }

    [Theory]
    [InlineData(1, (int)FrontResult.Ok)]
    [InlineData(2, (int)FrontResult.Cancel)]
    [InlineData(4, (int)FrontResult.Retry)]
    [InlineData(6, (int)FrontResult.Yes)]
    [InlineData(7, (int)FrontResult.No)]
    [InlineData(0, (int)FrontResult.Cancel)]
    public void 押されたものを読み替える(int id, int expected)
    {
        Assert.Equal((FrontResult)expected, FrontMessageBox.ToResult(id));
    }
}
