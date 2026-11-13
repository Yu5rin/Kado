using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

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

        // 「閉じる」ボタンも Esc も、フォーカスを動かさずに閉じる。配信元 URL や
        // いちばん細くできる幅は LostFocus で確定するので、放っておくと打った値が
        // 届く前に閉じてしまう。閉じ方によらず、ここで一度確定させる
        Closing += (_, _) => CommitFocusedBinding();

        // 指定された分類から開く。左の一覧を選び替えたら ViewModel に伝える（次に開くとき、そこから始める）
        var tabs = new (SettingsCategory Category, RadioButton Tab)[]
        {
            (SettingsCategory.Startup, TabStartup),
            (SettingsCategory.Edge, TabEdge),
            (SettingsCategory.Display, TabDisplay),
            (SettingsCategory.Workday, TabWorkday),
            (SettingsCategory.Notify, TabNotify),
            (SettingsCategory.Google, TabSync),
            (SettingsCategory.Data, TabData),
            (SettingsCategory.About, TabAbout),
        };

        foreach (var (category, tab) in tabs)
        {
            if (category == settings.Category) tab.IsChecked = true;

            tab.Checked += (_, _) => settings.Category = category;
        }

        // 本体の知らせの購読を外す
        Closed += (_, _) => settings.Detach();
    }

    /// <summary>いまフォーカスしている要素の Text 系バインディングを確定させる。</summary>
    private static void CommitFocusedBinding()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
