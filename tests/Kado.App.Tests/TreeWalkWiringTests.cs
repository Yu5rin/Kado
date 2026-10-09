namespace Kado.App.Tests;

/// <summary>
/// 押された要素から親をたどるとき、<c>VisualTreeHelper.GetParent</c> を直に呼ばないことを見張る。
/// <para>
/// 押された要素（<c>e.OriginalSource</c>）が <c>TextBlock</c> の中の <c>Run</c> だと、
/// <c>VisualTreeHelper.GetParent</c> は例外を投げる。右パネルのタスクの「↻」を押すと
/// アプリが落ちていた（v1.1.0）。親は必ず <c>TreeWalk.ParentOf</c> でたどる。
/// App プロジェクトはこのテストから参照できないので、ソースで見張る（<see cref="StaleDisplayWiringTests"/> と同じ理由）。
/// </para>
/// </summary>
public class TreeWalkWiringTests
{
    [Fact]
    public void 親をたどるのはTreeWalkだけが行う()
    {
        var app = Locate();
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("TreeWalk.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("VisualTreeHelper.GetParent(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(app, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Visualでない要素は論理ツリーの親へ上がる()
    {
        var source = File.ReadAllText(Path.Combine(Locate(), "Views", "TreeWalk.cs"));

        Assert.Contains("Visual or Visual3D => VisualTreeHelper.GetParent(node)", source, StringComparison.Ordinal);
        Assert.Contains("FrameworkContentElement content => content.Parent", source, StringComparison.Ordinal);
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kado.App");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Kado.App が見つかりません。");
    }
}
