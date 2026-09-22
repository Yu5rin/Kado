using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Presentation.Editing;

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

/// <summary>
/// 予定を削除する。元に戻すときは同じ内容で作り直す。
/// <para>
/// Google と結び付いていたら、消したことを記録する。<b>記録を残さないと、次の同期で
/// 復活する</b>。こちらで消しただけでは相手にはまだ残っていて、「こちらに無い予定」として
/// 降ってくるため。元に戻したときは記録も消す。消していないことになったのだから、
/// 相手へ伝えては困る。
/// </para>
/// </summary>
public sealed class DeleteEventEdit(
    EventRepository repository, CalendarEvent value, TombstoneRepository? tombstones = null)
    : IUndoableEdit
{
    public string Description => "予定の削除";

    public void Apply()
    {
        repository.Delete(value.Id);

        if (value.GoogleEventId is { Length: > 0 })
        {
            // 入れ先も一緒に控える。どのカレンダーのものか分からないと、同期のときに
            // 別のカレンダーへ削除を投げて 404 になり、削除が Google へ届かない
            tombstones?.Record(
                value.Id, TombstoneRepository.EventKind, value.GoogleEventId, DateTimeOffset.Now,
                value.CalendarId);
        }
    }

    public void Revert()
    {
        repository.Upsert(value);
        tombstones?.Clear(value.Id, TombstoneRepository.EventKind);
    }
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
    /// <para>
    /// <c>CompletedAt</c> も完了の切り替えに連動して変わる（項目3）ので、ここで一緒に
    /// 逃がす。逃がさないと、完了を切り替えただけなのに「タスクの変更」に化けてしまう。
    /// </para>
    /// </summary>
    public string Description =>
        before with { IsDone = after.IsDone, CompletedAt = after.CompletedAt, UpdatedAt = after.UpdatedAt } == after
            ? after.IsDone ? "タスクを完了にする" : "タスクの完了を取り消す"
            : "タスクの変更";

    public void Apply() => repository.Upsert(after);

    public void Revert() => repository.Upsert(before);
}

/// <summary>
/// タスクを削除する。
/// <para>作業時間ブロックも連鎖して消えるので、元に戻すときに入れ直す。</para>
/// </summary>
public sealed class DeleteTaskEdit(
    TaskRepository repository,
    TaskItem value,
    IReadOnlyList<WorkBlock> blocks,
    TombstoneRepository? tombstones = null)
    : IUndoableEdit
{
    public string Description => "タスクの削除";

    public void Apply()
    {
        repository.Delete(value.Id);

        // 記録を残さないと、次の同期で復活する
        if (value.GoogleTaskId is { Length: > 0 })
        {
            tombstones?.Record(
                value.Id, TombstoneRepository.TaskKind, value.GoogleTaskId, DateTimeOffset.Now,
                value.GoogleTaskListId ?? value.TaskListId);
        }
    }

    public void Revert()
    {
        repository.Upsert(value);
        foreach (var block in blocks) repository.UpsertBlock(block);

        tombstones?.Clear(value.Id, TombstoneRepository.TaskKind);
    }
}

/// <summary>
/// いくつかの編集を1手にまとめる。
/// <para>
/// 重複の整理のように、まとめて片付けたものは、まとめて戻せないと困る。
/// Ctrl＋Z を消した件数ぶん押させることになる。
/// </para>
/// </summary>
public sealed class CompositeEdit(string description, IReadOnlyList<IUndoableEdit> edits) : IUndoableEdit
{
    private readonly IReadOnlyList<IUndoableEdit> _edits =
        edits ?? throw new ArgumentNullException(nameof(edits));

    public string Description { get; } = description;

    public void Apply()
    {
        foreach (var edit in _edits) edit.Apply();
    }

    public void Revert()
    {
        // 戻すときは逆順。順に依存する編集が混ざっても筋が通る
        for (var i = _edits.Count - 1; i >= 0; i--) _edits[i].Revert();
    }
}
