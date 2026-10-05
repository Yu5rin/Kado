using System.Windows.Input;

namespace Kado.Presentation.Menus;

/// <summary>
/// 右クリックメニューの子メニューに並べる1件（「別のカレンダーへ移す」「期限を変える」など）。
/// <para>
/// 子メニューは別のポップアップに出て <c>ContextMenu</c> をたどれず、本体の ViewModel を引けない
/// （<see cref="Links.OpenMenuItem"/> と同じ事情）。押したときのコマンドと渡すものを、項目自身に持たせて渡す。
/// 押せないときは<b>隠さず灰色にして</b>、理由をツールチップに出す。
/// </para>
/// </summary>
/// <param name="Label">メニューに出す名前。</param>
/// <param name="Command">押したときのコマンド。</param>
/// <param name="Parameter">コマンドに渡すもの。</param>
/// <param name="Color">名前の前に出す色見本（<c>#rrggbb</c>）。無ければ出さない。</param>
/// <param name="IsEnabled">押せるか。</param>
/// <param name="ToolTip">押せない理由など。無ければ出さない。</param>
public sealed record MenuChoice(
    string Label,
    ICommand Command,
    object? Parameter,
    string? Color = null,
    bool IsEnabled = true,
    string? ToolTip = null)
{
    /// <summary>色見本を出すか。</summary>
    public bool HasColor => Color is { Length: > 0 };

    /// <summary>ツールチップを出すか。</summary>
    public bool HasToolTip => ToolTip is { Length: > 0 };
}
