namespace Kado.Presentation.Menus;

/// <summary>
/// クリップボードに文字を置く口。
/// <para>
/// ViewModel から直接 <c>Clipboard.SetText</c> を呼ぶと WPF に依存し、テストでも本物の
/// クリップボードを書き換えてしまう。<see cref="Links.ILinkOpener"/> と同じ考え方で、置く側を
/// 差し替えられるようにする。
/// </para>
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// 文字を置く。
    /// <para>Windows のクリップボードは、他のアプリが握っていると開けず失敗する。例外にせず false を返す。</para>
    /// </summary>
    /// <returns>置けたら true。</returns>
    bool TrySetText(string text);
}

/// <summary>何もしない実装。クリップボードの口を用意していない組み立て方（テストなど）で使う。</summary>
public sealed class NullClipboard : IClipboard
{
    public static readonly NullClipboard Instance = new();

    private NullClipboard() { }

    public bool TrySetText(string text) => true;
}
