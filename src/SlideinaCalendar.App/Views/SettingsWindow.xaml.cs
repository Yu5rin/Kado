using System.Windows;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 設定画面。
/// <para>変えたその場で効くので、ここには閉じる以外の操作を置かない。</para>
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        DataContext = settings;
    }
}
