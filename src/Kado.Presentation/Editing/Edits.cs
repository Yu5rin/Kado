using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Presentation.Editing;

/// <summary>
/// 予定を追加する。
/// <para>
/// 元に戻すときは消す。<b>Google と結び付いたあとなら（同期が ID を付けたあとなら）、
/// 消したことも記録する。</b>手元の行を消すだけでは Google に残り、次の同期で降ってきて
/// 復活する。使う人が Ctrl＋Z で消す意思を示しているので、Google からも消す。
/// </para>
/// <para>
/// やり直すときは、<b>Google の ID を持たない新しい予定</b>として入れる。消した Google 側は
/// 戻らない。古い ID のまま入れると、消えた相手と結び付いたつもりになって食い違い、
/// 新規として送ると同じものが Google に2つになりうる。
/// </para>
/// </summary>
public sealed class AddEventEdit(
    EventRepository repository, CalendarEvent value, TombstoneRepository? tombstones = null,
    string description = "予定の追加")
    : IUndoableEdit
{
    /// <summary>一度元に戻したか。戻したあとの「やり直し」だけ、新規として入れる。</summary>
    private bool _reverted;

    /// <summary>元に戻すメニューに出す説明。複製のときは「予定の複製」と言い分ける。</summary>
    public string Description => description;

    public void Apply() => repository.Upsert(_reverted ? SyncLinks.AsNew(value) : value);

    public void Revert()
    {
        _reverted = true;

        // 同期が結び付きを書いているかもしれないので、消す時点の最新の行を見る
        if (repository.Find(value.Id) is not { } latest) return;

        repository.Delete(value.Id);

        if (latest.GoogleEventId is { Length: > 0 } googleId)
        {
            tombstones?.Record(
                latest.Id, TombstoneRepository.EventKind, googleId, DateTimeOffset.Now,
                SyncLinks.OwnerOf(latest));
        }
    }
}

/// <summary>
/// 予定を書き換える。
/// <para>
/// 元に戻せるよう、書き換える前の姿を持っておく。書き換え後だけを持って
/// 「元に戻すときは読み直す」方式にすると、間に別の編集が入ったときに戻せない。
/// </para>
/// <para>
/// <b>書き込むのは「中身」だけを編集の前後から採り、Google との結び付き
/// （ID・入れ先・控えた生データ・更新時刻など）は書き込む時点の最新の行から採る。</b>
/// 同期は結び付きを書き換えている。古い結び付きで上書きすると、戻した内容が
/// 「同期済み」に見えて送られなかったり、移動を取り消すと実際の入れ先まで古い値に戻って
/// 次の編集が 404 → 作り直し（二重）になったりする。
/// </para>
/// </summary>
public sealed class UpdateEventEdit(
    EventRepository repository, CalendarEvent before, CalendarEvent after, string description = "予定の変更")
    : IUndoableEdit
{
    /// <summary>元に戻すメニューに出す説明。別のカレンダーへ移したときは、それが分かる言い方にする。</summary>
    public string Description => description;

    public void Apply() => Write(after, before);

    public void Revert() => Write(before, after);

    private void Write(CalendarEvent content, CalendarEvent other)
    {
        // 行が無い（向こうで消されて同期が手元からも消した、など）なら、書き戻さない。
        // ここで入れると、Google が消したものを手元だけが生き返らせてしまう
        if (repository.Find(content.Id) is not { } latest) return;

        repository.Upsert(SyncLinks.Merge(content, other, latest));
    }
}

/// <summary>
/// 予定を削除する。元に戻すときは同じ内容で作り直す。
/// <para>
/// Google と結び付いていたら、消したことを記録する。<b>記録を残さないと、次の同期で
/// 復活する</b>。こちらで消しただけでは相手にはまだ残っていて、「こちらに無い予定」として
/// 降ってくるため。元に戻したときは、まだ伝えていなければ記録も消して元通りにする。
/// 消していないことになったのだから、相手へ伝えては困る。
/// </para>
/// <para>
/// <b>もう伝え終えていたら</b>（記録が無い）、Google からは消えている。古い ID のまま手元にだけ
/// 戻すと、あるはずの相手が無く、次の編集が 404 になる。Google の ID を持たない新しい予定として
/// 作り直す（新規として送られる。二重にはならない）。
/// </para>
/// <para>
/// 記録の持ち主は、Google で<b>実際にいる</b>カレンダー（<c>GoogleCalendarId</c>）。入れ先の
/// 希望（<c>CalendarId</c>）を変えただけで、まだ move を送っていないときは両者が違う。
/// </para>
/// </summary>
public sealed class DeleteEventEdit(
    EventRepository repository, CalendarEvent value, TombstoneRepository? tombstones = null)
    : IUndoableEdit
{
    /// <summary>消した時点の姿。元に戻すときはこれに戻す。やり直しで消し直したら取り直す。</summary>
    private CalendarEvent? _removed;

    public string Description => "予定の削除";

    public void Apply()
    {
        // 消す時点の最新の行から結び付きを読む。呼び出し側が渡した姿は古いことがある
        var latest = repository.Find(value.Id) ?? _removed ?? value;

        _removed = latest;
        repository.Delete(value.Id);

        if (latest.GoogleEventId is { Length: > 0 } googleId)
        {
            tombstones?.Record(
                latest.Id, TombstoneRepository.EventKind, googleId, DateTimeOffset.Now,
                SyncLinks.OwnerOf(latest));
        }
    }

    public void Revert()
    {
        var restored = _removed ?? value;

        if (restored.GoogleEventId is { Length: > 0 } googleId && tombstones is not null)
        {
            // 同じ Google の ID を消す記録が残っていれば、まだ伝えていない
            if (tombstones.Find(restored.Id, TombstoneRepository.EventKind) is { } pending &&
                string.Equals(pending.GoogleId, googleId, StringComparison.Ordinal))
            {
                tombstones.Clear(restored.Id, TombstoneRepository.EventKind);
            }
            else
            {
                // もう伝え終えた。Google からは消えているので、新しい予定として作り直す
                restored = SyncLinks.AsNew(restored);
            }
        }

        repository.Upsert(restored);
    }
}

/// <summary>
/// タスクを追加する。
/// <para>元に戻す・やり直すときの扱いは <see cref="AddEventEdit"/> と同じ。</para>
/// </summary>
public sealed class AddTaskEdit(
    TaskRepository repository, TaskItem value, TombstoneRepository? tombstones = null,
    string description = "タスクの追加")
    : IUndoableEdit
{
    private bool _reverted;

    /// <summary>元に戻すメニューに出す説明。複製のときは「タスクの複製」と言い分ける。</summary>
    public string Description => description;

    public void Apply() => repository.Upsert(_reverted ? SyncLinks.AsNew(value) : value);

    public void Revert()
    {
        _reverted = true;

        if (repository.Find(value.Id) is not { } latest) return;

        repository.Delete(value.Id);

        if (latest.GoogleTaskId is { Length: > 0 } googleId)
        {
            tombstones?.Record(
                latest.Id, TombstoneRepository.TaskKind, googleId, DateTimeOffset.Now,
                SyncLinks.OwnerOf(latest));
        }
    }
}

/// <summary>
/// タスクを書き換える。
/// <para>中身と結び付きの分け方は <see cref="UpdateEventEdit"/> と同じ。</para>
/// </summary>
public sealed class UpdateTaskEdit(
    TaskRepository repository, TaskItem before, TaskItem after, string? description = null) : IUndoableEdit
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
        description ??
        (before with { IsDone = after.IsDone, CompletedAt = after.CompletedAt, UpdatedAt = after.UpdatedAt } == after
            ? after.IsDone ? "タスクを完了にする" : "タスクの完了を取り消す"
            : "タスクの変更");

    public void Apply() => Write(after);

    public void Revert() => Write(before);

    private void Write(TaskItem content)
    {
        // 行が無ければ書き戻さない（UpdateEventEdit と同じ理由）
        if (repository.Find(content.Id) is not { } latest) return;

        repository.Upsert(SyncLinks.Merge(content, latest));
    }
}

/// <summary>
/// タスクを削除する。元に戻すときは同じ内容で作り直す。
/// <para>Google との関わりは <see cref="DeleteEventEdit"/> と同じ。</para>
/// </summary>
public sealed class DeleteTaskEdit(
    TaskRepository repository,
    TaskItem value,
    TombstoneRepository? tombstones = null)
    : IUndoableEdit
{
    private TaskItem? _removed;

    public string Description => "タスクの削除";

    public void Apply()
    {
        var latest = repository.Find(value.Id) ?? _removed ?? value;

        _removed = latest;
        repository.Delete(value.Id);

        // 記録を残さないと、次の同期で復活する
        if (latest.GoogleTaskId is { Length: > 0 } googleId)
        {
            tombstones?.Record(
                latest.Id, TombstoneRepository.TaskKind, googleId, DateTimeOffset.Now,
                SyncLinks.OwnerOf(latest));
        }
    }

    public void Revert()
    {
        var restored = _removed ?? value;

        if (restored.GoogleTaskId is { Length: > 0 } googleId && tombstones is not null)
        {
            if (tombstones.Find(restored.Id, TombstoneRepository.TaskKind) is { } pending &&
                string.Equals(pending.GoogleId, googleId, StringComparison.Ordinal))
            {
                tombstones.Clear(restored.Id, TombstoneRepository.TaskKind);
            }
            else
            {
                restored = SyncLinks.AsNew(restored);
            }
        }

        repository.Upsert(restored);
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
