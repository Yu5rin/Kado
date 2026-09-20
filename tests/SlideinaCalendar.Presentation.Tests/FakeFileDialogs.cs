using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>ファイル選択の代わり。開かずに、あらかじめ決めたパスを返す。</summary>
internal sealed class FakeFileDialogs : IFileDialogs
{
    /// <summary>選ばせたことにするパス。null なら取り消し。</summary>
    public string? FileToPick { get; set; }

    /// <summary>保存先として返すパス。null なら取り消し。</summary>
    public string? FileToSave { get; set; }

    /// <summary>保存先として渡された既定の名前。</summary>
    public string? LastSuggestedName { get; private set; }

    /// <summary>確認にどう答えるか。</summary>
    public bool Confirms { get; set; } = true;

    /// <summary>最後に出した結果。</summary>
    public string? LastReport { get; private set; }

    public string? LastReportTitle { get; private set; }

    /// <summary>ファイルを選ばせようとしたか。</summary>
    public bool WasAskedForFile { get; private set; }

    public string? PickOpenFile(string title, string filter)
    {
        WasAskedForFile = true;
        return FileToPick;
    }

    public string? PickSaveFile(string title, string filter, string suggestedName)
    {
        LastSuggestedName = suggestedName;
        return FileToSave;
    }

    public bool Confirm(string title, string message) => Confirms;

    public void ShowReport(string title, string message)
    {
        LastReportTitle = title;
        LastReport = message;
    }
}
