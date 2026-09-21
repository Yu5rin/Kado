using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>期限の早入れ1つ。</summary>
/// <param name="Label">「明日」。</param>
/// <param name="Date">その日。</param>
public sealed record DuePreset(string Label, DateOnly Date);

/// <summary>
/// タスクの編集内容。
/// <para>
/// 項目は Google Tasks に合わせてある（タイトル＝title、詳細＝notes、期限＝due、
/// 完了＝status）。Google Tasks の期限は日付だけで時刻を持たないので、こちらも
/// 時刻は置かない。
/// </para>
/// <para>
/// 期限は「決まっていない」を持てる（要件書 3.1）。日付欄を空にできない代わりに、
/// 期限を付けるかどうかの切り替えを別に持つ。
/// </para>
/// </summary>
public sealed class TaskEditorViewModel : ObservableObject
{
    /// <summary>
    /// タイトル（Google Tasks の <c>title</c>）の上限。
    /// <para>Google Tasks API の公式リファレンスに明記されている値（1024文字）。</para>
    /// </summary>
    private const int TitleMaxLength = 1024;

    /// <summary>
    /// 詳細（Google Tasks の <c>notes</c>）の上限。
    /// <para>Google Tasks API の公式リファレンスに明記されている値（8192文字）。</para>
    /// </summary>
    private const int NoteMaxLength = 8192;

    private readonly TaskItem? _original;
    private readonly DateOnly _today;

    private string _title = string.Empty;
    private bool _hasDue = true;
    private DateOnly _due;
    private bool _isDone;
    private string? _note;
    private string? _taskListId;

    /// <summary>
    /// 新しく作る。
    /// <para>
    /// <paramref name="defaultTaskListId"/> は呼び出し側（<c>SourceListsViewModel.DefaultTaskList</c>）
    /// が決めた入れ先。渡さなければ一覧の先頭にするが、これは<b>同期対象外のローカル
    /// リストに固定されうる</b>ので、呼び出し側は極力渡すこと（項目2）。
    /// </para>
    /// </summary>
    public TaskEditorViewModel(DateOnly due, IReadOnlyList<SourceChoice> taskLists, DateOnly today,
        string? defaultTaskListId = null)
    {
        TaskLists = taskLists;
        _today = today;
        _due = due;
        _taskListId = defaultTaskListId ?? (taskLists.Count > 0 ? taskLists[0].Id : null);
    }

    /// <summary>すでにあるタスクを直す。</summary>
    public TaskEditorViewModel(TaskItem value, IReadOnlyList<SourceChoice> taskLists, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(value);

        _original = value;
        TaskLists = taskLists;
        _today = today;

        _title = value.Title;
        _hasDue = value.HasDue;
        // 期限が無いタスクに付けると決めたときの初期値
        _due = value.Due ?? today;
        _isDone = value.IsDone;
        _note = value.Note;
        _taskListId = value.TaskListId;
    }

    /// <summary>期限の早入れ。日付欄を開かずに決められる。</summary>
    public IReadOnlyList<DuePreset> DuePresets =>
    [
        new("今日", _today),
        new("明日", _today.AddDays(1)),
        new("来週", _today.AddDays(7)),
    ];

    public bool IsNew => _original is null;

    public string HeaderText => IsNew ? "タスクの追加" : "タスクの編集";

    /// <summary>
    /// 削除を求めて閉じたか。
    /// <para>
    /// 呼び出し側（<see cref="IEditorPresenter"/> の実装）が画面を閉じたあとにこれを見て、
    /// 実際の削除（確認ダイアログを含む）を行う。編集画面そのものは削除を実行しない。
    /// </para>
    /// </summary>
    public bool Deleted { get; private set; }

    /// <summary>
    /// 削除して閉じることを求める。既存のタスクを編集しているときだけ効く。
    /// <para>新規作成の途中では消すものが無いので、呼んでも何もしない。</para>
    /// </summary>
    public void RequestDelete()
    {
        if (IsNew) return;

        Deleted = true;
    }

    /// <summary>選べるタスクリスト。</summary>
    public IReadOnlyList<SourceChoice> TaskLists { get; }

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

    /// <summary>期限を入れる。早入れのボタンから呼ぶ。期限なしだったら付ける。</summary>
    public void SetDue(DateOnly date)
    {
        Due = date;
        HasDue = true;
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    /// <summary>詳細。Google Tasks の <c>notes</c>。</summary>
    public string? Note
    {
        get => _note;
        set
        {
            // 長さの上限に引っかかるかで保存できるかが変わるので、出し直す
            if (Set(ref _note, value)) Raise(nameof(CanSave), nameof(ValidationMessage));
        }
    }

    public string? TaskListId
    {
        get => _taskListId;
        set => Set(ref _taskListId, value);
    }

    public bool CanSave => ValidationMessage is null;

    public string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_title)) return "タイトルを入れてください。";

            // 長すぎるタイトルはそのタスクリスト全体の同期を止める原因になる。入口で止める
            if (_title.Length > TitleMaxLength) return $"タイトルは{TitleMaxLength}文字以内にしてください。";

            if (_note is { Length: > 0 } && _note.Length > NoteMaxLength)
                return $"詳細は{NoteMaxLength}文字以内にしてください。";

            return null;
        }
    }

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
            // 完了にしているときだけ入れる。もともとの完了日時があれば（Google 側の値を
            // 含む）そのまま残し、無ければ今にする。外していれば消す（項目3）
            CompletedAt = _isDone ? _original?.CompletedAt ?? DateTimeOffset.Now : null,
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
