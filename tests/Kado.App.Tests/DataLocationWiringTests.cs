using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 保存先が <c>DataLocation</c> 1か所を通っていることを、ソースを読んで見張る。
/// <para>
/// 散らばった <c>Environment.GetFolderPath(LocalApplicationData)</c> が残ると、
/// 保存先を <c>data</c> に移したのに、一部のファイル（ログ・トークン・バックアップなど）だけ
/// 従来の場所に書き続けてしまい、フォルダごとコピーしても持っていけなくなる。
/// </para>
/// </summary>
public class DataLocationWiringTests
{
    [Fact]
    public void LocalApplicationDataを直に使うのはDataLocationだけ()
    {
        var offenders = SourceFiles()
            .Where(file => !file.EndsWith(Path.Combine("Kado.Data", "DataLocation.cs"), StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bLocalApplicationData\b|\bCommonApplicationData\b"))
            .ToList();

        Assert.True(offenders.Count == 0, "DataLocation を通していない: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DefaultPathのフォルダを自前で求めていない()
    {
        // 保存先のフォルダは DataLocation.Directory から取る
        var offenders = SourceFiles()
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"GetDirectoryName\(\s*(Kado\.Data\.)?CalendarDatabase\.DefaultPath"))
            .ToList();

        Assert.True(offenders.Count == 0, "DataLocation.Directory を使うこと: " + string.Join(", ", offenders));
    }

    [Fact]
    public void 起動の最初に保存先を決めてからデータベースやログを開く()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

        var acquire = source.IndexOf("SingleInstance.TryAcquire", StringComparison.Ordinal);
        var resolve = source.IndexOf("DataLocation.EnsureResolved()", StringComparison.Ordinal);
        var recover = source.IndexOf("WorkAreaGuard.RecoverIfNeeded", StringComparison.Ordinal);
        var openDb = source.IndexOf("OpenDatabaseOrAskUser)", StringComparison.Ordinal);

        Assert.True(acquire >= 0 && resolve > acquire, "単一起動の確保のあとで保存先を決めること");
        Assert.True(recover > resolve, "ワークエリアの印（保存先に置く）より先に決めること");
        Assert.True(openDb > resolve, "データベースを開く前に決めること");
    }

    [Fact]
    public void 保存先を決めたときの行をshell_logに残している()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

        Assert.Matches(new Regex(@"location\.Notes.*?ShellDiagnosticsLog\.Write", RegexOptions.Singleline), source);
    }

    [Fact]
    public void 自動更新の置き場所は保存先のupdates()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory, "Update", "UpdateService.cs"));

        Assert.Matches(new Regex(@"StagedDirectory\s*=>\s*Path\.Combine\(Kado\.Data\.DataLocation\.Directory,\s*""updates""\)"), source);
    }

    // ------------------------------------------------------------------

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string SrcDirectory { get; } = Locate("src");

    private static string AppDirectory { get; } = Path.Combine(SrcDirectory, "Kado.App");

    private static string Locate(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, name);
            if (Directory.Exists(Path.Combine(candidate, "Kado.App"))) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src が見つかりません。");
    }
}
