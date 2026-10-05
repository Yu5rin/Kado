using System.Windows;
using Microsoft.Win32;
using Kado.App.Views;
using Kado.Presentation.Editing;

namespace Kado.App.Editing;

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

    public IReadOnlyList<string> PickOpenFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = true,
        };

        return dialog.ShowDialog(ownerProvider()) == true ? dialog.FileNames : [];
    }

    public IReadOnlyList<string> PickFolders(string title)
    {
        // .NET 8 から WPF にもフォルダ選びがある（WinForms の FolderBrowserDialog は借りない。
        // UseWindowsForms を足すと型名が衝突する。docs/README.md の「トレイ」の項と同じ理由）
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = true,
        };

        return dialog.ShowDialog(ownerProvider()) == true ? dialog.FolderNames : [];
    }

    public string? PickSaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            OverwritePrompt = true,
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
