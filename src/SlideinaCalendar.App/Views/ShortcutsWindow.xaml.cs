using System.Windows;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>ショートカットの一覧。読むだけなので、閉じる以外の操作を置かない。</summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        DataContext = Shortcuts.All;
    }
}
