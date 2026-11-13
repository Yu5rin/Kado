namespace Kado.Presentation.Infrastructure;

/// <summary>
/// フォーカスのある部品が、どのキーを自分で使うか。
/// <para>
/// 本体のキー割当（<c>MainWindow.OnPreviewKeyDown</c>）は、Enter・矢印・Delete を「選んでいる日を動かす」
/// 「予定を足す」「行を消す」に使う。フォーカスがボタンや仕切りやメニューにあるときまで奪うと、
/// Enter でボタンが押せず、仕切りを矢印で動かせず、スライダーも動かせない。
/// </para>
/// <para>
/// 部品の型は WPF のものなので、ここ（WPF に依存しない層）では<b>型名</b>で判断する。
/// 継承をたどって、名前が一覧にあれば当てはまる。試験では偽の型で確かめられる。
/// </para>
/// </summary>
public static class KeyFocusPolicy
{
    /// <summary>文字を打ち込む部品。修飾キー無しの単独キー（T・数字・Home など）も文字として受ける。</summary>
    private static readonly HashSet<string> TextInputTypes = new(StringComparer.Ordinal)
    {
        "TextBoxBase",
        "ComboBox",
        "PasswordBox",
    };

    /// <summary>
    /// 自分で Enter・Space・矢印・Delete を使う操作部品。
    /// <para>ボタン類（チェック・ラジオ・トグルを含む）・仕切り・メニュー・選択の一覧・スライダー・スクロールバー・つまみ。</para>
    /// </summary>
    private static readonly HashSet<string> OperableTypes = new(StringComparer.Ordinal)
    {
        "ButtonBase",
        "GridSplitter",
        "Thumb",
        "MenuBase",
        "MenuItem",
        "Selector",
        "ListBoxItem",
        "TabItem",
        "Slider",
        "ScrollBar",
        "TreeView",
        "TreeViewItem",
        "DataGrid",
        "Expander",
        "DatePicker",
        "Calendar",
    };

    /// <summary>文字を打ち込む部品か。</summary>
    public static bool IsTextInput(Type? focused) => Matches(focused, TextInputTypes);

    /// <summary>
    /// 自分で Enter・Space・矢印・Delete を使う操作部品か。
    /// <para>文字入力の部品も含む（それらは文字を打つために同じキーを使う）。</para>
    /// </summary>
    public static bool TakesNavigationKeys(Type? focused) =>
        Matches(focused, TextInputTypes) || Matches(focused, OperableTypes);

    private static bool Matches(Type? type, HashSet<string> names)
    {
        for (var node = type; node is not null; node = node.BaseType)
        {
            if (names.Contains(node.Name)) return true;
        }

        return false;
    }
}
