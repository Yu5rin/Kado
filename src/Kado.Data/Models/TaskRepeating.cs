using Kado.Core.Recurrence;

namespace Kado.Data.Models;

/// <summary>
/// タスクの繰り返し（<see cref="TaskItem.Repeat"/>）に関する、タスクそのものを組み立てる決まり。
/// <para>
/// 完了にしたときの次の回づくりは2か所から呼ばれる。Kado の中で完了にしたとき
/// （<c>CalendarWorkspace</c>）と、Google 側で完了にされたと同期で分かったとき
/// （<c>TaskSyncEngine</c>）。次の回の中身を決めるのがここの1か所なので、どちらから作っても同じになる。
/// </para>
/// </summary>
public static class TaskRepeating
{
    /// <summary>
    /// 繰り返しを付けられないタスクか。期限の無いタスクと、サブタスク。
    /// <para>
    /// 期限が無いと次の回の日が決まらない。サブタスクは親の下に並ぶもので、次の回を作ると親子が崩れる。
    /// </para>
    /// </summary>
    public static bool CannotRepeat(DateOnly? due, string? parentId) =>
        due is null || parentId is { Length: > 0 };

    /// <summary>
    /// 保存するタスクの繰り返しを、付けられる姿に整える。
    /// <list type="bullet">
    ///   <item>期限が無い・サブタスクなら外す（期限を消したら繰り返しも外れる）</item>
    ///   <item>期限日が変わったら、暦どおりの4つは新しい期限日の曜日・日付で指定し直す</item>
    /// </list>
    /// 読めない指定は、付けられる条件を満たしている限り<b>消さずに</b>そのまま返す。
    /// </summary>
    /// <param name="value">保存しようとしているタスク。</param>
    /// <param name="previous">いま保存されている姿（新規なら null）。期限日が変わったかを見る。</param>
    public static TaskItem Normalize(TaskItem value, TaskItem? previous)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Repeat is null) return value;

        // サブタスクかは、いま保存されている行を信じる（親子は Google が持つので、編集画面の写しは古いことがある）
        var parentId = previous is null ? value.ParentId : previous.ParentId;

        if (CannotRepeat(value.Due, parentId)) return value with { Repeat = null };

        if (previous is not null && previous.Due != value.Due && value.Due is { } due)
        {
            return value with { Repeat = TaskRepeat.Respec(value.Repeat, due) };
        }

        return value;
    }

    /// <summary>
    /// 完了にしたタスクの「次の回」を作る。次の回が無ければ null。
    /// <para>
    /// 題名・メモ・タスクリスト・URL・添付・繰り返しの指定を引き継ぐ。期限は
    /// <see cref="TaskRepeat.NextDue"/> で求めた日。Google の ID などの結び付きは持たない
    /// （新しいタスクとして同期で送られる）。<b>並び順（<c>SortOrder</c>）は呼び出し側が決める</b>
    /// （同じ期限日の末尾。リポジトリを見る必要があるため）。
    /// </para>
    /// <para>
    /// 次の回が無いのは、繰り返しが無い・読めない・期限が無い・サブタスク・日が見つからないとき。
    /// </para>
    /// </summary>
    /// <param name="completed">完了にしたタスク（<see cref="TaskItem.Repeat"/> と <see cref="TaskItem.Due"/> を見る）。</param>
    /// <param name="today">今日。</param>
    /// <param name="isWorkday">稼働日か。渡さなければ土日祝を除いた日。</param>
    /// <param name="weekStart">週の始まりの曜日（設定）。</param>
    /// <param name="now">今。次の回の作成日時・更新時刻になる。</param>
    public static TaskItem? NextOccurrence(
        TaskItem completed, DateOnly today, Func<DateOnly, bool>? isWorkday, DayOfWeek weekStart, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (completed.Repeat is null || completed.Due is not { } due) return null;
        if (CannotRepeat(due, completed.ParentId)) return null;

        if (TaskRepeat.NextDue(completed.Repeat, due, today, isWorkday, weekStart) is not { } next) return null;

        return new TaskItem
        {
            Id = NewId(),
            Title = completed.Title,
            Due = next,
            IsDone = false,
            Note = completed.Note,
            Url = completed.Url,
            Attachments = completed.Attachments,

            // 指定は書き換えずに持ち越す。31日の月が28日に寄っても、次はまた31日に戻る
            Repeat = completed.Repeat,
            TaskListId = completed.TaskListId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// 次の回として作ろうとしているタスクと同じものが、すでにあるか探す。
    /// <para>
    /// 2台で使うと、片方で完了にした次の回が同期でもう片方へ届いたあと、そちらでも同じタスクを
    /// 完了にして（または完了の知らせを受け取って）次の回をもう一度作り、二重になる。
    /// 作る前に、<b>同じリスト・同じ題名・同じ期限で、未完了</b>のタスクを探す。Kado の中の完了でも
    /// 同期経由の完了でも、この1か所を通す。
    /// </para>
    /// </summary>
    /// <param name="candidates">期限が次の回と同じ日のタスク（それ以外が混ざっていてもよい）。</param>
    /// <param name="next"><see cref="NextOccurrence"/> で求めた次の回。</param>
    /// <returns>すでにあるタスク。無ければ null。</returns>
    public static TaskItem? FindExistingNext(IEnumerable<TaskItem> candidates, TaskItem next)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(next);

        return candidates.FirstOrDefault(t =>
            !t.IsDone &&
            !string.Equals(t.Id, next.Id, StringComparison.Ordinal) &&
            t.Due == next.Due &&
            string.Equals(t.TaskListId ?? string.Empty, next.TaskListId ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(t.Title, next.Title, StringComparison.Ordinal));
    }

    /// <summary>
    /// すでにある次の回に、繰り返しを引き継がせる。すでに自分の繰り返しを持つなら、そのまま。
    /// </summary>
    public static TaskItem WithRepeatOf(TaskItem existingNext, TaskItem next) =>
        existingNext.Repeat is null
            ? existingNext with { Repeat = next.Repeat, UpdatedAt = next.UpdatedAt }
            : existingNext;

    /// <summary>完了にしたほうのタスクから、繰り返しを外す（取り消して完了し直しても次の回が二重にならない）。</summary>
    public static TaskItem WithoutRepeat(TaskItem completed) => completed with { Repeat = null };

    private static string NewId() => Guid.NewGuid().ToString("N")[..15];
}
