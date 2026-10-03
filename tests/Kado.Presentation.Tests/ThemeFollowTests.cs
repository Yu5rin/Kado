using Kado.Presentation.Settings;

namespace Kado.Presentation.Tests;

/// <summary>
/// 配色が「自動」のとき、Windows の明暗の切り替えについていくかどうかの判断。
/// <para>
/// 画面を作り直すのは重いので、システムの設定変更の通知（General / Color は無関係な変更でも
/// 何度も来る）のたびには払わない。明暗が実際に変わったときだけ当て直す。
/// </para>
/// </summary>
public class ThemeFollowTests
{
    [Fact]
    public void 自動のときはシステムの明暗に従う()
    {
        Assert.Equal(ThemeChoice.Dark, ThemeFollow.Resolve(ThemeChoice.Auto, ThemeChoice.Dark));
        Assert.Equal(ThemeChoice.Light, ThemeFollow.Resolve(ThemeChoice.Auto, ThemeChoice.Light));
    }

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Night)]
    public void 自動でなければ選んだ配色のまま(ThemeChoice chosen) =>
        Assert.Equal(chosen, ThemeFollow.Resolve(chosen, ThemeChoice.Dark));

    [Fact]
    public void 自動でシステムの明暗が変わったときだけ当て直す()
    {
        // ライトで当ててあるところへ、システムがダークになった
        Assert.True(ThemeFollow.ShouldReapply(ThemeChoice.Auto, ThemeChoice.Light, ThemeChoice.Dark));
    }

    [Fact]
    public void 自動でも明暗が変わっていなければ当て直さない()
    {
        // 無関係なシステム設定の変更でも通知は来る
        Assert.False(ThemeFollow.ShouldReapply(ThemeChoice.Auto, ThemeChoice.Dark, ThemeChoice.Dark));
    }

    [Theory]
    [InlineData(ThemeChoice.Light)]
    [InlineData(ThemeChoice.Dark)]
    [InlineData(ThemeChoice.Night)]
    public void 配色を選んであるときはシステムが変わっても当て直さない(ThemeChoice chosen) =>
        Assert.False(ThemeFollow.ShouldReapply(chosen, chosen, ThemeChoice.Dark));
}
