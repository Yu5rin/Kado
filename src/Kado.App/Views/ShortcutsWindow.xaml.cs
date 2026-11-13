using System.Windows;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>
/// ショートカットの一覧。読むだけなので、閉じる以外の操作を置かない。
/// <para>モードレスで出す（見ながら本体のキーを試せる）。閉じるのは「閉じる」ボタンと Esc。</para>
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        DataContext = Shortcuts.All;

        ModelessWindow.CloseOnEscape(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
