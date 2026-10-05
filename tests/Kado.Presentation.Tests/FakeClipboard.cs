using Kado.Presentation.Menus;

namespace Kado.Presentation.Tests;

/// <summary>クリップボードの代わり。本物を書き換えず、置かれた文字だけを控える。</summary>
internal sealed class FakeClipboard : IClipboard
{
    /// <summary>最後に置かれた文字。何も置かれていなければ null。</summary>
    public string? Text { get; private set; }

    /// <summary>置いた回数。</summary>
    public int Count { get; private set; }

    /// <summary>false にすると、置くのに失敗する（他のアプリがクリップボードを掴んでいる、など）。</summary>
    public bool Succeeds { get; set; } = true;

    public bool TrySetText(string text)
    {
        if (!Succeeds) return false;

        Text = text;
        Count++;
        return true;
    }
}
