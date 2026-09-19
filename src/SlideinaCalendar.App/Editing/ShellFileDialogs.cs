using System.Windows;
using Microsoft.Win32;
using SlideinaCalendar.App.Views;
using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.App.Editing;

/// <summary>
/// ファイル選択と結果表示を OS のダイアログと窓で行う。
/// <para>ViewModel は <see cref="IFileDialogs"/> しか知らないので、WPF への依存はここで止まる。</para>
/// </summary>
public sealed class ShellFileDialogs(Func<Window?> ownerProvider) : IFileDialogs
{
    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(ownerProvider()) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            ownerProvider() ?? Application.Current.MainWindow,
            message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void ShowReport(string title, string message) =>
        new ReportWindow(title, message) { Owner = ownerProvider() }.ShowDialog();
}
