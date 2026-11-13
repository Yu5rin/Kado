using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kado.App.Views;

/// <summary>
/// モードレス（<c>Show()</c> で出す）の窓に共通の、閉じ方。
/// <para>
/// <c>IsCancel</c> は、<c>ShowDialog()</c> で出した窓でしか窓を閉じない。モードレスの窓では
/// 「閉じる」ボタンも Esc も何も起きないので、ボタンに <c>Click</c> を付けて <c>Close()</c> し、
/// 窓の Esc でも閉じる。実働日計算とショートカットの一覧が使う。
/// </para>
/// </summary>
internal static class ModelessWindow
{
    /// <summary>窓に Esc で閉じる処理を付ける。開いている選択肢の一覧がある間は、その Esc を一覧に譲る。</summary>
    internal static void CloseOnEscape(Window window)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            // 一覧を開いているときの Esc は、一覧を閉じるためのもの
            if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;

            window.Close();
            e.Handled = true;
        };
    }
}
