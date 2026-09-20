namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// ファイルを選ばせたり、結果を見せたりする口。
/// <para>
/// ViewModel から直接ダイアログを開くと WPF に依存して試験できなくなる。
/// <see cref="IEditorPresenter"/> と同じ考え方で、出す側を差し替えられるようにする。
/// </para>
/// </summary>
public interface IFileDialogs
{
    /// <summary>開くファイルを選ばせる。取り消されたら null。</summary>
    /// <param name="title">ダイアログの見出し。</param>
    /// <param name="filter">WPF の <c>OpenFileDialog.Filter</c> と同じ書式。</param>
    string? PickOpenFile(string title, string filter);

    /// <summary>保存先を選ばせる。取り消されたら null。</summary>
    /// <param name="title">ダイアログの見出し。</param>
    /// <param name="filter">WPF の <c>SaveFileDialog.Filter</c> と同じ書式。</param>
    /// <param name="suggestedName">既定のファイル名。</param>
    string? PickSaveFile(string title, string filter, string suggestedName);

    /// <summary>取り返しのつかない操作の前に尋ねる。</summary>
    bool Confirm(string title, string message);

    /// <summary>取り込みの結果やログを見せる。長くなるので折り返して出す。</summary>
    void ShowReport(string title, string message);
}

/// <summary>何も出さない実装。取り込みの口を用意していない画面で使う。</summary>
public sealed class NullFileDialogs : IFileDialogs
{
    public static readonly NullFileDialogs Instance = new();

    private NullFileDialogs() { }

    public string? PickOpenFile(string title, string filter) => null;

    public string? PickSaveFile(string title, string filter, string suggestedName) => null;

    public bool Confirm(string title, string message) => false;

    public void ShowReport(string title, string message) { }
}
