using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 編集画面の代わり。開かずに、渡された入力欄をその場で埋めて返す。
/// <para>これがないと MainViewModel の編集まわりを WPF 無しでは試せない。</para>
/// </summary>
internal sealed class FakeEditorPresenter : IEditorPresenter
{
    /// <summary>予定の編集画面が開かれたときに呼ぶ。true を返すと保存する。</summary>
    public Func<EventEditorViewModel, bool>? OnEvent { get; set; }

    /// <summary>タスクの編集画面が開かれたときに呼ぶ。</summary>
    public Func<TaskEditorViewModel, bool>? OnTask { get; set; }

    /// <summary>カレンダーの編集画面が開かれたときに呼ぶ。</summary>
    public Func<CalendarEditorViewModel, bool>? OnCalendar { get; set; }

    /// <summary>削除の確認にどう答えるか。</summary>
    public bool ConfirmsDelete { get; set; } = true;

    /// <summary>文言つきの確認にどう答えるか。</summary>
    public bool Confirms { get; set; } = true;

    /// <summary>直近に開かれた予定の入力欄。開かれたかどうかの確認に使う。</summary>
    public EventEditorViewModel? LastEventEditor { get; private set; }

    public TaskEditorViewModel? LastTaskEditor { get; private set; }

    /// <summary>直近に開かれたカレンダーの入力欄。</summary>
    public CalendarEditorViewModel? LastCalendarEditor { get; private set; }

    /// <summary>直近に出した確認の本文。何を尋ねたかの確認に使う。</summary>
    public string? LastConfirmMessage { get; private set; }

    /// <summary>削除の確認に出された名前。</summary>
    public string? LastConfirmedTitle { get; private set; }

    public bool ShowEventEditor(EventEditorViewModel editor)
    {
        LastEventEditor = editor;
        return OnEvent?.Invoke(editor) ?? false;
    }

    public bool ShowTaskEditor(TaskEditorViewModel editor)
    {
        LastTaskEditor = editor;
        return OnTask?.Invoke(editor) ?? false;
    }

    public bool ShowCalendarEditor(CalendarEditorViewModel editor)
    {
        LastCalendarEditor = editor;
        return OnCalendar?.Invoke(editor) ?? false;
    }

    public bool ConfirmDelete(string title)
    {
        LastConfirmedTitle = title;
        return ConfirmsDelete;
    }

    public bool Confirm(string title, string message)
    {
        LastConfirmedTitle = title;
        LastConfirmMessage = message;
        return Confirms;
    }
}
