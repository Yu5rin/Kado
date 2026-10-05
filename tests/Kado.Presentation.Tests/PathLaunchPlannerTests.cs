using Kado.Presentation.Links;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクに添えたファイルの場所を、どう開くかの判断。起動はせず、判断だけを見る。
/// 実行形式は実行しない（入っているフォルダを開いて、そのファイルを選んだ状態にする）。
/// </summary>
public class PathLaunchPlannerTests
{
    private static readonly Func<string, bool> Nothing = _ => false;

    private static Func<string, bool> Only(params string[] paths) =>
        candidate => paths.Contains(candidate, StringComparer.Ordinal);

    [Fact]
    public void ふつうのファイルはシェルで開く()
    {
        var plan = PathLaunchPlanner.Plan(@"C:\資料\図面.pdf", Only(@"C:\資料\図面.pdf"), Nothing);

        Assert.Equal(PathLaunchOutcome.OpenFile, plan.Outcome);
        Assert.Equal(new LaunchRequest(LaunchKind.Shell, @"C:\資料\図面.pdf"), plan.Request);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void フォルダはエクスプローラーで開く()
    {
        var plan = PathLaunchPlanner.Plan(@"\\server\share\議事録", Nothing, Only(@"\\server\share\議事録"));

        Assert.Equal(PathLaunchOutcome.OpenFolder, plan.Outcome);
        Assert.Equal(new LaunchRequest(LaunchKind.Shell, @"\\server\share\議事録"), plan.Request);
    }

    [Fact]
    public void 見つからなければ開かずに場所を添えて知らせる()
    {
        var plan = PathLaunchPlanner.Plan(@"C:\消えた\図面.pdf", Nothing, Nothing);

        Assert.Equal(PathLaunchOutcome.NotFound, plan.Outcome);
        Assert.Null(plan.Request);
        Assert.False(plan.Launches);
        Assert.Equal(@"見つかりません：C:\消えた\図面.pdf", plan.Message);
    }

    [Theory]
    [InlineData(@"C:\tools\setup.exe")]
    [InlineData(@"C:\tools\a.com")]
    [InlineData(@"C:\tools\a.bat")]
    [InlineData(@"C:\tools\a.cmd")]
    [InlineData(@"C:\tools\a.ps1")]
    [InlineData(@"C:\tools\a.vbs")]
    [InlineData(@"C:\tools\a.vbe")]
    [InlineData(@"C:\tools\a.js")]
    [InlineData(@"C:\tools\a.jse")]
    [InlineData(@"C:\tools\a.wsf")]
    [InlineData(@"C:\tools\a.wsh")]
    [InlineData(@"C:\tools\a.msi")]
    [InlineData(@"C:\tools\a.msp")]
    [InlineData(@"C:\tools\a.scr")]
    [InlineData(@"C:\tools\a.lnk")]
    [InlineData(@"C:\tools\a.cpl")]
    [InlineData(@"C:\tools\a.hta")]
    [InlineData(@"C:\tools\a.reg")]
    [InlineData(@"C:\tools\a.pif")]
    public void 実行形式は実行せず入っているフォルダを開いて選んだ状態にする(string path)
    {
        var plan = PathLaunchPlanner.Plan(path, Only(path), Nothing);

        Assert.Equal(PathLaunchOutcome.RevealFile, plan.Outcome);
        Assert.Equal(new LaunchRequest(LaunchKind.Reveal, path), plan.Request);

        // 実行しなかったことが使う人に伝わる
        Assert.NotNull(plan.Message);
    }

    [Theory]
    [InlineData(@"C:\tools\SETUP.EXE")]
    [InlineData(@"C:\tools\Run.Bat")]
    [InlineData(@"C:\tools\readme.txt.exe")]
    [InlineData(@"C:\tools\setup.exe.")]
    [InlineData(@"C:\tools\setup.exe ")]
    [InlineData(@"C:\tools\setup.exe. .")]
    [InlineData("C:/tools/setup.exe")]
    [InlineData(@"\\server\share\setup.exe")]
    public void 実行形式の見分けは大文字小文字と末尾のドットや空白に惑わされない(string path)
    {
        // Windows は名前の末尾のドット・空白を無視する（setup.exe. は setup.exe として動く）
        var trimmed = path.Trim();
        var plan = PathLaunchPlanner.Plan(path, Only(trimmed), Nothing);

        Assert.Equal(PathLaunchOutcome.RevealFile, plan.Outcome);
    }

    [Theory]
    [InlineData(@"C:\資料\図面.pdf")]
    [InlineData(@"C:\資料\report.docx")]
    [InlineData(@"C:\資料\data.csv")]
    [InlineData(@"C:\資料\exe")]
    [InlineData(@"C:\資料\a.exe\readme.txt")]
    [InlineData(@"C:\資料\setup")]
    public void 実行形式でないものはふつうに開く(string path)
    {
        Assert.False(PathLaunchPlanner.IsExecutable(path));

        var plan = PathLaunchPlanner.Plan(path, Only(path), Nothing);

        Assert.Equal(PathLaunchOutcome.OpenFile, plan.Outcome);
    }

    [Fact]
    public void 拡張子が実行形式に見えるフォルダは実行ではなく開くだけ()
    {
        // 「手順.exe」という名前のフォルダ。中身を見せるだけなので問題ない
        var plan = PathLaunchPlanner.Plan(@"C:\資料\手順.exe", Nothing, Only(@"C:\資料\手順.exe"));

        Assert.Equal(PathLaunchOutcome.OpenFolder, plan.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"資料\図面.pdf")]
    [InlineData(@".\図面.pdf")]
    [InlineData(@"..\..\Windows\System32\calc.exe")]
    [InlineData("図面.pdf")]
    [InlineData("C:図面.pdf")]
    [InlineData(@"\Windows\System32")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\?\C:\a.txt")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"C:\資料\図面.pdf:evil.exe")]
    [InlineData(@"C:\資料\a""b.txt")]
    [InlineData(@"C:\資料\a|b.txt")]
    [InlineData(@"C:\資料\a*.txt")]
    [InlineData("C:\\資料\\a\nb.txt")]
    [InlineData("https://example.com/a.pdf")]
    [InlineData("file:///C:/a.txt")]
    public void フルパスでない書き方や危ない書き方は存在を確かめる前に止める(string? path)
    {
        var asked = false;
        bool Probe(string _)
        {
            asked = true;
            return true;
        }

        var plan = PathLaunchPlanner.Plan(path, Probe, Probe);

        Assert.Equal(PathLaunchOutcome.Invalid, plan.Outcome);
        Assert.Null(plan.Request);
        Assert.False(asked);
    }

    [Fact]
    public void エクスプローラーに渡す引数は常にパスを引用符で包む()
    {
        Assert.Equal("""/select,"C:\a b\c.exe" """.TrimEnd(), PathLaunchPlanner.RevealArguments(@"C:\a b\c.exe"));
    }

    [Fact]
    public void 前後の空白は落として開く()
    {
        var plan = PathLaunchPlanner.Plan(@"  C:\資料\図面.pdf  ", Only(@"C:\資料\図面.pdf"), Nothing);

        Assert.Equal(PathLaunchOutcome.OpenFile, plan.Outcome);
        Assert.Equal(@"C:\資料\図面.pdf", plan.Request!.Target);
    }
}
