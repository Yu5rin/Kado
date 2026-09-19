using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// タスクの編集内容。
/// <para>
/// 期限は「決まっていない」を持てる（要件書 3.1）。日付欄を空にできない代わりに、
/// 期限を付けるかどうかの切り替えを別に持つ。
/// </para>
/// </summary>
public sealed class TaskEditorViewModel : ObservableObject
{
    private readonly TaskItem? _original;

    private string _title = string.Empty;
    private bool _hasDue = true;
    private DateOnly _due;
    private bool _isDone;
    private string? _note;
    private string? _taskListId;

    /// <summary>新しく作る。</summary>
    public TaskEditorViewModel(DateOnly due, IReadOnlyList<string> taskLists)
    {
        TaskLists = taskLists;
        _due = due;
        _taskListId = taskLists.Count > 0 ? taskLists[0] : null;
    }

    /// <summary>すでにあるタスクを直す。</summary>
    public TaskEditorViewModel(TaskItem value, IReadOnlyList<string> taskLists, DateOnly fallbackDue)
    {
        ArgumentNullException.ThrowIfNull(value);

        _original = value;
        TaskLists = taskLists;

        _title = value.Title;
        _hasDue = value.HasDue;
        _due = value.Due ?? fallbackDue;
        _isDone = value.IsDone;
        _note = value.Note;
        _taskListId = value.TaskListId;
    }

    public bool IsNew => _original is null;

    public string HeaderText => IsNew ? "タスクの追加" : "タスクの編集";

    /// <summary>選べるタスクリスト。</summary>
    public IReadOnlyList<string> TaskLists { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value ?? string.Empty)) Raise(nameof(CanSave), nameof(ValidationMessage));
        }
    }

    /// <summary>期限を付けるか。外すと「いつやるか未定」になる。</summary>
    public bool HasDue
    {
        get => _hasDue;
        set => Set(ref _hasDue, value);
    }

    public DateOnly Due
    {
        get => _due;
        set => Set(ref _due, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    public string? Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public string? TaskListId
    {
        get => _taskListId;
        set => Set(ref _taskListId, value);
    }

    public bool CanSave => ValidationMessage is null;

    public string? ValidationMessage =>
        string.IsNullOrWhiteSpace(_title) ? "タイトルを入れてください。" : null;

    /// <summary>入力からタスクを組み立てる。</summary>
    public TaskItem ToModel()
    {
        if (ValidationMessage is { } message) throw new InvalidOperationException(message);

        return new TaskItem
        {
            Id = _original?.Id ?? NewId(),
            Title = _title.Trim(),
            Due = _hasDue ? _due : null,
            IsDone = _isDone,
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note.Trim(),
            TaskListId = _taskListId,

            // Google 側の情報は編集画面で触らない
            GoogleTaskId = _original?.GoogleTaskId,
            GoogleTaskListId = _original?.GoogleTaskListId,
            GoogleUpdated = _original?.GoogleUpdated,
            Source = _original?.Source,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..15];
}
