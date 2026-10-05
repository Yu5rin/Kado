using Kado.Presentation.Links;

namespace Kado.Presentation.Tests;

/// <summary>
/// 開いてよい URL の決まり。予定の URL・予定の添付・タスクの URL を開くところが、
/// すべてこの決まりを通る。
/// </summary>
public class LinkRulesTests
{
    [Theory]
    [InlineData("https://example.com/spec", "https://example.com/spec")]
    [InlineData("http://example.com/spec", "http://example.com/spec")]
    [InlineData("  https://example.com/a?b=c#d  ", "https://example.com/a?b=c#d")]
    [InlineData("HTTPS://EXAMPLE.COM/", "https://example.com/")]
    [InlineData("https://drive.google.com/file/d/ID/view?usp=drivesdk", "https://drive.google.com/file/d/ID/view?usp=drivesdk")]
    public void リンクはhttpとhttpsだけ開ける(string text, string expected)
    {
        Assert.Equal(expected, LinkRules.WebUrl(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("www.example.com")]
    [InlineData("example.com/spec")]
    [InlineData("ftp://example.com/spec")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"file://server/share/a.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("mailto:someone@example.com")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("https://")]
    [InlineData("http:///x")]
    [InlineData("https://example.com/a\nb")]
    public void それ以外は開かない(string? text)
    {
        Assert.Null(LinkRules.WebUrl(text));
        Assert.Null(LinkRules.SecureUrl(text));
    }

    [Fact]
    public void 添付はhttpsだけ開ける()
    {
        Assert.Equal("https://example.com/a", LinkRules.SecureUrl("https://example.com/a"));
        Assert.True(LinkRules.IsHttps("https://example.com/a"));

        // 暗号化されない通信で、ドライブのファイルを開かせない
        Assert.Null(LinkRules.SecureUrl("http://example.com/a"));
        Assert.False(LinkRules.IsHttps("http://example.com/a"));
    }
}
