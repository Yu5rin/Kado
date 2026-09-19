using System.Windows;
using SlideinaCalendar.App.Views;
using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.App.Editing;

/// <summary>
/// 編集画面をダイアログとして出す。
/// <para>ViewModel は <see cref="IEditorPresenter"/> しか知らないので、WPF への依存はここで止まる。</para>
/// </summary>
public sealed class DialogEditorPresenter(Func<Window?> ownerProvider) : IEditorPresenter
{
    public bool ShowEventEditor(EventEditorViewModel editor) =>
        Show(new EventEditorWindow(editor));

    public bool ShowTaskEditor(TaskEditorViewModel editor) =>
        Show(new TaskEditorWindow(editor));

    public bool ShowCalendarEditor(CalendarEditorViewModel editor) =>
        Show(new CalendarEditorWindow(editor));

    public bool ConfirmDelete(string title) =>
        MessageBox.Show(
            ownerProvider() ?? Application.Current.MainWindow,
            $"「{title}」を削除します。よろしいですか。\n\n削除しても Ctrl＋Z で元に戻せます。",
            "SlideinaCalendar", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            ownerProvider() ?? Application.Current.MainWindow,
            message,
            title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private bool Show(Window window)
    {
        // 親を渡さないと画面の真ん中ではなく前回の位置に出る
        window.Owner = ownerProvider();
        return window.ShowDialog() == true;
    }
}
