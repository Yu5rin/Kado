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

    public void ShowSettings(SlideinaCalendar.Presentation.ViewModels.SettingsViewModel settings)
    {
        // 設定は変えたその場で効くので、閉じ方（OK・取り消し）は見ない
        Show(new SettingsWindow(settings));
    }

    /// <summary>いま開いている実働日計算パネル。二重に開かないよう、あれば前面に出すだけにする。</summary>
    private WorkdayCalculatorWindow? _workdayCalculatorWindow;

    public void ShowWorkdayCalculator(
        SlideinaCalendar.Presentation.ViewModels.WorkdayCalculatorViewModel calculator)
    {
        // すでに開いていれば、それを前面に出すだけ。数えている途中の内容も保たれる
        if (_workdayCalculatorWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        // モードレス。カレンダーを見ながら期間を確かめられるようにする（要件書 4.5）。
        // 親を設定するので、本体を閉じれば一緒に閉じる
        var window = new WorkdayCalculatorWindow(calculator) { Owner = ownerProvider() };

        _workdayCalculatorWindow = window;
        window.Closed += (_, _) =>
        {
            _workdayCalculatorWindow = null;
            calculator.NotifyClosed();
        };

        window.Show();
    }

    public void ShowShortcuts() => Show(new ShortcutsWindow());

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
