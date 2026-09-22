namespace Kado.Presentation.Editing;

/// <summary>
/// 編集画面を出す口。
/// <para>
/// ViewModel から直接ダイアログを開くと WPF に依存して試験できなくなる。
/// 出す側を差し替えられるようにして、テストでは開かずに結果だけ返す。
/// </para>
/// </summary>
public interface IEditorPresenter
{
    /// <summary>予定の編集画面を出す。</summary>
    /// <returns>保存されたら true、取り消されたら false。</returns>
    bool ShowEventEditor(EventEditorViewModel editor);

    /// <summary>タスクの編集画面を出す。</summary>
    /// <returns>保存されたら true、取り消されたら false。</returns>
    bool ShowTaskEditor(TaskEditorViewModel editor);

    /// <summary>カレンダーまたはタスクリストの編集画面を出す。</summary>
    /// <returns>保存されたら true、取り消されたら false。</returns>
    bool ShowCalendarEditor(CalendarEditorViewModel editor);

    /// <summary>削除してよいか尋ねる。元に戻せるとはいえ、取り返しのつかない操作に見える。</summary>
    /// <returns>削除してよければ true。</returns>
    bool ConfirmDelete(string title);

    /// <summary>文言を指定して尋ねる。消したあとの行き先など、断りが要るとき。</summary>
    /// <returns>進めてよければ true。</returns>
    bool Confirm(string title, string message);

    /// <summary>設定画面を出す。変えたその場で効くので、結果は返さない。</summary>
    void ShowSettings(ViewModels.SettingsViewModel settings);

    /// <summary>実働日計算パネルを出す。数えるだけなので、結果は返さない。</summary>
    void ShowWorkdayCalculator(ViewModels.WorkdayCalculatorViewModel calculator);

    /// <summary>ショートカットの一覧を出す。読むだけ。</summary>
    void ShowShortcuts();
}

/// <summary>何も出さない実装。編集画面を用意していない画面で使う。</summary>
public sealed class NullEditorPresenter : IEditorPresenter
{
    public static readonly NullEditorPresenter Instance = new();

    private NullEditorPresenter() { }

    public bool ShowEventEditor(EventEditorViewModel editor) => false;

    public bool ShowTaskEditor(TaskEditorViewModel editor) => false;

    public bool ShowCalendarEditor(CalendarEditorViewModel editor) => false;

    public bool ConfirmDelete(string title) => false;

    public bool Confirm(string title, string message) => false;

    public void ShowSettings(ViewModels.SettingsViewModel settings) { }

    public void ShowWorkdayCalculator(ViewModels.WorkdayCalculatorViewModel calculator) { }

    public void ShowShortcuts() { }
}
