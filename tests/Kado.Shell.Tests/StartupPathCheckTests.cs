using Kado.App.Settings;

namespace Kado.Shell.Tests;

/// <summary>
/// Windows ログオン時の自動起動の登録が、いまの exe の場所を指しているか。
/// exe と data をまとめて別の場所へコピーしたとき、登録を書き直すかどうかの判断。
/// </summary>
public class StartupPathCheckTests
{
    [Fact]
    public void 引用符でくくった登録からパスを取り出す()
    {
        Assert.Equal(@"C:\Apps\Kado\Kado.exe", StartupPathCheck.ExtractPath("\"C:\\Apps\\Kado\\Kado.exe\""));
    }

    [Fact]
    public void 引用符が無い登録は全体をパスと見なす()
    {
        Assert.Equal(@"C:\Apps\Kado.exe", StartupPathCheck.ExtractPath(@"C:\Apps\Kado.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 登録が無ければ自動起動は使っていないので書き直さない(string? registered)
    {
        Assert.Null(StartupPathCheck.ExtractPath(registered));
        Assert.False(StartupPathCheck.NeedsRewrite(registered, "/apps/Kado.exe"));
    }

    [Fact]
    public void 登録の場所がいまと違えば書き直す()
    {
        Assert.True(StartupPathCheck.NeedsRewrite("\"/old/Kado.exe\"", "/new/Kado.exe"));
    }

    [Fact]
    public void 登録の場所がいまと同じなら書き直さない()
    {
        Assert.False(StartupPathCheck.NeedsRewrite("\"/apps/Kado.exe\"", "/apps/Kado.exe"));
    }

    [Fact]
    public void 閉じていない引用符は読めないので触らない()
    {
        Assert.Null(StartupPathCheck.ExtractPath("\"/apps/Kado.exe"));
        Assert.False(StartupPathCheck.NeedsRewrite("\"/apps/Kado.exe", "/new/Kado.exe"));
    }
}
