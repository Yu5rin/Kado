using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 実働日計算パネル。
/// <para>数えるだけなので、閉じる以外の操作を置かない。</para>
/// </summary>
public partial class WorkdayCalculatorWindow : Window
{
    public WorkdayCalculatorWindow(WorkdayCalculatorViewModel calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);

        InitializeComponent();
        DataContext = calculator;

        // 「閉じる」も Esc も、フォーカスを動かさずに閉じる。いまは実働日数欄が
        // PropertyChanged で確定するので実害は無いが、閉じ方によらず確定させておく
        Closing += (_, _) => CommitFocusedBinding();
    }

    /// <summary>いまフォーカスしている要素の Text 系バインディングを確定させる。</summary>
    private static void CommitFocusedBinding()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
