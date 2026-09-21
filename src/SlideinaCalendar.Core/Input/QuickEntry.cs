namespace SlideinaCalendar.Core.Input;

/// <summary>
/// クイック入力の1行が、予定かタスクか。
/// <para>
/// 既定は予定。先頭に「□」「-」「todo」「タスク:」のような印が付いていればタスクと読む
/// （<see cref="QuickParser"/>）。
/// </para>
/// </summary>
public enum QuickEntryKind
{
    Event,
    Task,
}

/// <summary>
/// 1行から読み取った予定・タスクの下書き。
/// </summary>
/// <param name="Title">残った文字。予定・タスクの題になる。</param>
/// <param name="Date">読み取った日。書かれていなければ基準日のまま。タスクなら期限日として使う。</param>
/// <param name="Start">開始時刻。書かれていなければ null（終日）。タスクは時刻を持たないので使わない。</param>
/// <param name="End">終了時刻。片方しか書かれていなければ null。</param>
/// <param name="Location">「@」のあとに書かれた場所。</param>
/// <param name="HasDateError">日付として読めたが、暦に無い日だった（2月30日など）。</param>
/// <param name="HasTimeError">時刻として読めたが、24時を超えるなど無い時刻だった。</param>
/// <param name="UnsupportedWord">
/// 読み取れない言い回し。「毎週」など。
/// <para>
/// 一部だけ解釈して登録すると、書いたつもりと違うものが入る。見つかったら止める。
/// </para>
/// </param>
/// <param name="Kind">予定として入れるか、タスクとして入れるか。</param>
public sealed record QuickEntry(
    string Title,
    DateOnly Date,
    TimeOnly? Start,
    TimeOnly? End,
    string? Location,
    bool HasDateError,
    bool HasTimeError,
    string? UnsupportedWord,
    QuickEntryKind Kind = QuickEntryKind.Event)
{
    /// <summary>そのまま登録してよいか。</summary>
    public bool CanCommit =>
        Title.Length > 0 && !HasDateError && !HasTimeError && UnsupportedWord is null;

    /// <summary>タスクとして読んだか。</summary>
    public bool IsTask => Kind == QuickEntryKind.Task;
}
