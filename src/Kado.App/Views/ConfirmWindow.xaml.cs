using System.Windows;

namespace Kado.App.Views;

/// <summary>ボタンの文言を指定できる確認の窓。</summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string acceptLabel, string cancelLabel)
    {
        InitializeComponent();

        Title = title;
        Heading.Text = title;
        Body.Text = message;
        AcceptButton.Content = acceptLabel;
        CancelButton.Content = cancelLabel;
    }

    /// <summary>尋ねる。進める側のボタンが押されたら true。×・Esc・取り消す側のボタンは false。</summary>
    /// <param name="owner">持ち主の窓。なければ画面の中央に出る。</param>
    public static bool Ask(Window? owner, string title, string message, string acceptLabel, string cancelLabel)
    {
        var window = new ConfirmWindow(title, message, acceptLabel, cancelLabel);

        if (owner is { IsLoaded: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
