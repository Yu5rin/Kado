using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// 予定を追加する。
/// <para>元に戻すときは消す。追加した予定の識別子は保持しておく。</para>
/// </summary>
public sealed class AddEventEdit(EventRepository repository, CalendarEvent value) : IUndoableEdit
{
    public string Description => "予定の追加";

    public void Apply() => repository.Upsert(value);

    public void Revert() => repository.Delete(value.Id);
}

/// <summary>
/// 予定を書き換える。
/// <para>
/// 元に戻せるよう、書き換える前の姿を持っておく。書き換え後だけを持って
/// 「元に戻すときは読み直す」方式にすると、間に別の編集が入ったときに戻せない。
/// </para>
/// </summary>
public sealed class UpdateEventEdit(EventRepository repository, CalendarEvent before, CalendarEvent after)
    : IUndoableEdit
{
    public string Description => "予定の変更";

    public void Apply() => repository.Upsert(after);

    public void Revert() => repository.Upsert(before);
}

/// <summary>予定を削除する。元に戻すときは同じ内容で作り直す。</summary>
public sealed class DeleteEventEdit(EventRepository repository, CalendarEvent value) : IUndoableEdit
{
    public string Description => "予定の削除";

    public void Apply() => repository.Delete(value.Id);

    public void Revert() => repository.Upsert(value);
}

/// <summary>タスクを追加する。</summary>
public sealed class AddTaskEdit(TaskRepository repository, TaskItem value) : IUndoableEdit
{
    public string Description => "タスクの追加";

    public void Apply() => repository.Upsert(value);

    public void Revert() => repository.Delete(value.Id);
}

/// <summary>タスクを書き換える。</summary>
public sealed class UpdateTaskEdit(TaskRepository repository, TaskItem before, TaskItem after) : IUndoableEdit
{
    /// <summary>
    /// 完了の切り替えだけなら、その旨を説明に出す。
    /// 「タスクの変更を元に戻しますか」より「完了の取り消し」のほうが何が起きるか分かる。
    /// </summary>
    public string Description => before with { IsDone = after.IsDone, UpdatedAt = after.UpdatedAt } == after
        ? after.IsDone ? "タスクを完了にする" : "タスクの完了を取り消す"
        : "タスクの変更";

    public void Apply() => repository.Upsert(after);

    public void Revert() => repository.Upsert(before);
}

/// <summary>
/// タスクを削除する。
/// <para>作業時間ブロックも連鎖して消えるので、元に戻すときに入れ直す。</para>
/// </summary>
public sealed class DeleteTaskEdit(TaskRepository repository, TaskItem value, IReadOnlyList<WorkBlock> blocks)
    : IUndoableEdit
{
    public string Description => "タスクの削除";

    public void Apply() => repository.Delete(value.Id);

    public void Revert()
    {
        repository.Upsert(value);
        foreach (var block in blocks) repository.UpsertBlock(block);
    }
}
