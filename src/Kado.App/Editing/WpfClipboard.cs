using System.Runtime.InteropServices;
using System.Windows;
using Kado.Presentation.Menus;

namespace Kado.App.Editing;

/// <summary>
/// クリップボードへ文字を置く（右クリックメニューの「題名と日時をコピー」）。
/// <para>
/// ViewModel は <see cref="IClipboard"/> しか知らないので、WPF への依存はここで止まる。
/// 他のアプリがクリップボードを掴んでいると開けず <see cref="COMException"/> になる。
/// 例外にせず false を返し、画面側が「コピーできませんでした」と伝える。
/// </para>
/// </summary>
public sealed class WpfClipboard : IClipboard
{
    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
