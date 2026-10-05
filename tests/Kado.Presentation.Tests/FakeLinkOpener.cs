using Kado.Presentation.Links;

namespace Kado.Presentation.Tests;

/// <summary>URL やファイルを開く代わり。何も起動せず、起動を頼まれた内容だけを控える。</summary>
internal sealed class FakeLinkOpener : ILinkOpener
{
    /// <summary>起動を頼まれたもの。頼まれた順。</summary>
    public List<LaunchRequest> Launched { get; } = [];

    /// <summary>設定すると、起動を頼まれたときにこの例外を投げる（ブラウザが無い・関連付けが無い、など）。</summary>
    public Exception? Throws { get; set; }

    public void Launch(LaunchRequest request)
    {
        if (Throws is { } error) throw error;

        Launched.Add(request);
    }
}
