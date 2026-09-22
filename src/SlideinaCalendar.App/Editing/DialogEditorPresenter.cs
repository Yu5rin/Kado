using System.Windows;
using SlideinaCalendar.App.Shell;
using SlideinaCalendar.App.Views;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Editing;

/// <summary>
/// 編集画面をダイアログとして出す。
/// <para>ViewModel は <see cref="IEditorPresenter"/> しか知らないので、WPF への依存はここで止まる。</para>
/// </summary>
public sealed class DialogEditorPresenter(Func<Window?> ownerProvider) : IEditorPresenter
{
    public bool ShowEventEditor(EventEditorViewModel editor) =>
        ShowEditorWindow(new EventEditorWindow(editor));

    public bool ShowTaskEditor(TaskEditorViewModel editor) =>
        ShowEditorWindow(new TaskEditorWindow(editor));

    public bool ShowCalendarEditor(CalendarEditorViewModel editor) =>
        ShowEditorWindow(new CalendarEditorWindow(editor));

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

    /// <summary>
    /// 予定・タスク・カレンダーの編集画面を出す。
    /// <para>
    /// スライド／ピン留めで本体が帯として出ているときは、帯に重ねず、帯の外側の
    /// 隣に上端を揃えて出す（実機の報告。帯の中身が隠れて日付や一覧を見ながら
    /// 入力できなかった）。ウィンドウで出しているときは、今までどおり親の中央
    /// （XAML の既定 <c>WindowStartupLocation="CenterOwner"</c>）のまま変えない。
    /// </para>
    /// <para>
    /// 設定画面・実働日計算パネルは対象外。設定は720×560と編集画面よりずっと
    /// 大きく帯の隣に収まらないため、実働日計算はモードレス（<see cref="ShowWorkdayCalculator"/>）
    /// で、この <see cref="Show"/> 経由の仕組みをそもそも使わないため。
    /// </para>
    /// </summary>
    private bool ShowEditorWindow(Window window)
    {
        var owner = ownerProvider();

        // Owner は必ず設定する（外さない）。スライドが引っ込まない判定
        // （ShellController.OwnsForeground）と、本体を閉じたとき一緒に閉じる仕組みが
        // これに依っている
        window.Owner = owner;

        if (owner is not null && owner.DataContext is MainViewModel { Shell.IsAtEdge: true } main)
        {
            // 幅は XAML で固定（Width="440" など）だが、高さは SizeToContent="Height"
            // なので、この時点ではまだ確定していない。ネイティブ窓を作った直後
            // （SourceInitialized）まで待つ。WindowStartupLocation を Manual にしないと、
            // 確定後に Left/Top を入れても CenterOwner の計算に上書きされる
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += (_, _) => PositionBesideBand(window, owner, main.Shell);
        }

        return window.ShowDialog() == true;
    }

    /// <summary>帯の外側・上端揃えの位置を計算して <paramref name="window"/> に入れる。</summary>
    private static void PositionBesideBand(Window window, Window owner, ShellViewModel shell)
    {
        var work = Screens.WorkAreaDips(owner);
        var screen = new EditorWindowPlacement.Rect(work.Left, work.Top, work.Width, work.Height);

        // 帯の位置と幅は本体ウィンドウの Left／ActualWidth（実際に出ている幅）から取る
        var band = new EditorWindowPlacement.Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);

        var (left, top) = EditorWindowPlacement.NextToBand(
            screen, band, shell.Edge, window.ActualWidth, window.ActualHeight);

        window.Left = left;
        window.Top = top;
    }
}
