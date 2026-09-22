using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// XAML のリソース参照を、アプリを起動せずに検査する。
/// <para>
/// WPF は Windows でしか動かせず、CI は Linux で回す。ビルドが通っても
/// 読み込み時に落ちる書き方があり、実際に2度落としている。
/// </para>
/// <para>
/// とくに <c>StaticResource</c> と <c>DynamicResource</c> は<b>値をそのまま返し、
/// 型変換を挟まない</b>。文字列リソースを <c>Path.Data</c> に渡すと
/// <c>Geometry</c> にならず、画面を組み立てる前に落ちる。
/// </para>
/// </summary>
public class XamlResourceTests
{
    /// <summary>参照のしかた。キーと、渡し先のプロパティ名を持つ。</summary>
    private sealed record Reference(string Key, string Property, string File);

    /// <summary>x:Key を持つ要素を拾う。1 が要素名、2 がキー。</summary>
    private const string KeyPattern = """<(\w+)\b[^>]*?x:Key="([^"]+)""";

    private static readonly string[] Brushes =
        ["SolidColorBrush", "LinearGradientBrush", "ImageBrush"];

    /// <summary>
    /// 渡し先のプロパティ名 → そこに入れてよい定義要素。
    /// <para>継承は見ず、実際に使っている型だけを挙げる。増えたらここに足す。</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> Expected = new(StringComparer.Ordinal)
    {
        ["Data"] = ["PathGeometry", "StreamGeometry", "EllipseGeometry", "RectangleGeometry", "GeometryGroup"],
        ["Background"] = Brushes,
        ["Foreground"] = Brushes,
        ["BorderBrush"] = Brushes,
        ["Fill"] = Brushes,
        ["Stroke"] = Brushes,
        ["CaretBrush"] = Brushes,
        ["Color"] = ["Color"],
        ["Style"] = ["Style"],
        ["BasedOn"] = ["Style"],
        ["ItemTemplate"] = ["DataTemplate"],
        ["FontFamily"] = ["FontFamily"],
        ["ContextMenu"] = ["ContextMenu"],
    };

    [Fact]
    public void 参照しているリソースキーはすべて定義されている()
    {
        var defined = Definitions();

        var missing = References()
            .Where(r => !defined.ContainsKey(r.Key))
            .Select(r => $"{r.File}: {r.Property}=\"{{...Resource {r.Key}}}\"")
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void リソースの型は渡し先のプロパティに合っている()
    {
        var defined = Definitions();

        var mismatched = References()
            .Where(r => Expected.ContainsKey(r.Property) && defined.TryGetValue(r.Key, out var element))
            .Where(r => !Expected[r.Property].Contains(defined[r.Key], StringComparer.Ordinal))
            .Select(r => $"{r.File}: {r.Property} に {r.Key}（{defined[r.Key]}）")
            .ToArray();

        Assert.Empty(mismatched);
    }

    [Fact]
    public void コードから引いているリソースキーも定義されている()
    {
        var defined = Definitions();

        // TryFindResource("...") に渡している文字列。綴り違いは実行するまで分からない
        var missing = Directory
            .EnumerateFiles(AppDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(NotGenerated)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"TryFindResource\(""([^""]+)""\)")
                .Select(m => (File: Relative(f), Key: m.Groups[1].Value)))
            .Where(x => !defined.ContainsKey(x.Key))
            .Select(x => $"{x.File}: {x.Key}")
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void アイコン辞書の中身はすべてジオメトリになっている()
    {
        // Path.Data に渡すためだけの辞書。文字列を置くと読み込み時に落ちる
        var icons = Regex
            .Matches(File.ReadAllText(Path.Combine(AppDirectory, "Themes", "Icons.xaml")), KeyPattern)
            .Select(m => (Element: m.Groups[1].Value, Key: m.Groups[2].Value))
            .ToArray();

        Assert.NotEmpty(icons);
        Assert.All(icons, icon => Assert.Contains(icon.Element, Expected["Data"]));
    }

    // ------------------------------------------------------------------

    /// <summary>x:Key → 定義している要素名。</summary>
    private static Dictionary<string, string> Definitions()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in XamlFiles())
        foreach (Match match in Regex.Matches(File.ReadAllText(file), KeyPattern))
        {
            result[match.Groups[2].Value] = match.Groups[1].Value;
        }

        return result;
    }

    /// <summary>XAML から拾った参照。</summary>
    private static IEnumerable<Reference> References()
    {
        foreach (var file in XamlFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(
                text, """(\w+)="\{(?:Static|Dynamic)Resource\s+([\w.]+)\}"""))
            {
                yield return new Reference(match.Groups[2].Value, match.Groups[1].Value, Relative(file));
            }

            // <Setter Property="Foreground" Value="{StaticResource ...}" /> は
            // 渡し先が Value ではなく Property の側にある
            foreach (Match match in Regex.Matches(
                text,
                """Property="([\w.]+)"\s+Value="\{(?:Static|Dynamic)Resource\s+([\w.]+)\}"""))
            {
                var property = match.Groups[1].Value;
                yield return new Reference(
                    match.Groups[2].Value, property[(property.LastIndexOf('.') + 1)..], Relative(file));
            }
        }
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories).Where(NotGenerated);

    private static bool NotGenerated(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string path) =>
        Path.GetRelativePath(AppDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>App プロジェクトの場所。テストの出力先から上へたどって探す。</summary>
    private static string AppDirectory { get; } = Locate();

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
