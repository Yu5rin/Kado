namespace Kado.Presentation.Menus;

/// <summary>
/// 予定の右クリックメニューを出し分けるための情報。右クリックされた1件について、
/// <c>MainViewModel.EventMenuFor</c> が組み立てる。
/// <para>
/// 押せない項目は<b>隠さず灰色にして</b>、理由をツールチップに出す（理由が分からないのが一番困る）。
/// 理由を持つのは、読み取り専用のカレンダー・Google 側で変えられない予定のように、こちらで変えても
/// 向こうへ伝わらないもの。
/// </para>
/// </summary>
public sealed class EventMenuInfo
{
    /// <summary>予定が見つからなかったとき。項目はそのまま押せる（押すと「見つかりませんでした」と出る）。</summary>
    public static readonly EventMenuInfo Unavailable = new(
        canEdit: true, duplicateDisabledReason: null,
        moveChoices: [], moveDisabledReason: "移し先にできるカレンダーがありません",
        deleteDisabledReason: null);

    internal EventMenuInfo(
        bool canEdit,
        string? duplicateDisabledReason,
        IReadOnlyList<MenuChoice> moveChoices,
        string? moveDisabledReason,
        string? deleteDisabledReason)
    {
        CanEdit = canEdit;
        DuplicateDisabledReason = duplicateDisabledReason;
        MoveChoices = moveChoices;
        MoveDisabledReason = moveDisabledReason;
        DeleteDisabledReason = deleteDisabledReason;
    }

    /// <summary>
    /// 編集できる予定か。できなければ、項目は「詳細を見る」になり、読み取り専用の画面が開く。
    /// </summary>
    public bool CanEdit { get; }

    /// <summary>先頭の項目の名前。編集できなければ「詳細を見る」。</summary>
    public string EditLabel => CanEdit ? "編集" : "詳細を見る";

    /// <summary>複製できない理由。できるなら null。</summary>
    public string? DuplicateDisabledReason { get; }

    /// <summary>複製できるか。</summary>
    public bool CanDuplicate => DuplicateDisabledReason is null;

    /// <summary>「別のカレンダーへ移す」の子メニュー。今のカレンダーと、書き込めないカレンダーは含まない。</summary>
    public IReadOnlyList<MenuChoice> MoveChoices { get; }

    /// <summary>「別のカレンダーへ移す」を押せない理由。押せるなら null。</summary>
    public string? MoveDisabledReason { get; }

    /// <summary>「別のカレンダーへ移す」を押せるか。</summary>
    public bool CanMove => MoveDisabledReason is null;

    /// <summary>削除できない理由。できるなら null。</summary>
    public string? DeleteDisabledReason { get; }

    /// <summary>削除できるか。</summary>
    public bool CanDelete => DeleteDisabledReason is null;
}

/// <summary>
/// タスクの右クリックメニューを出し分けるための情報。<c>MainViewModel.TaskMenuFor</c> が組み立てる。
/// </summary>
public sealed class TaskMenuInfo
{
    /// <summary>タスクが見つからなかったとき。</summary>
    public static readonly TaskMenuInfo Unavailable = new(
        doneLabel: "完了にする", dueChoices: [], listChoices: [],
        listMoveDisabledReason: "移し先にできるリストがありません");

    internal TaskMenuInfo(
        string doneLabel,
        IReadOnlyList<MenuChoice> dueChoices,
        IReadOnlyList<MenuChoice> listChoices,
        string? listMoveDisabledReason)
    {
        DoneLabel = doneLabel;
        DueChoices = dueChoices;
        ListChoices = listChoices;
        ListMoveDisabledReason = listMoveDisabledReason;
    }

    /// <summary>完了の項目の名前。完了済みなら「完了を取り消す」、そうでなければ「完了にする」。</summary>
    public string DoneLabel { get; }

    /// <summary>「期限を変える」の子メニュー。いまの期限と同じ項目は灰色。</summary>
    public IReadOnlyList<MenuChoice> DueChoices { get; }

    /// <summary>「別のリストへ移す」の子メニュー。今のリストと、入れられないリストは含まない。</summary>
    public IReadOnlyList<MenuChoice> ListChoices { get; }

    /// <summary>「別のリストへ移す」を押せない理由。押せるなら null。</summary>
    public string? ListMoveDisabledReason { get; }

    /// <summary>「別のリストへ移す」を押せるか。</summary>
    public bool CanMoveToList => ListMoveDisabledReason is null;
}

/// <summary>
/// 日付・空き時間の右クリックメニューを出し分けるための情報。<c>MainViewModel.DayMenuFor</c> が組み立てる。
/// <para>「この日を1日で見る」「この週を見る」「この月を見る」は、今いるビューと同じものを出さない。</para>
/// </summary>
public sealed class DayMenuInfo
{
    internal DayMenuInfo(DayTarget target, bool showsDay, bool showsWeek, bool showsMonth)
    {
        Target = target;
        ShowsDay = showsDay;
        ShowsWeek = showsWeek;
        ShowsMonth = showsMonth;
    }

    /// <summary>開かれた場所（日と、時間帯なら時刻）。「この日に予定を追加」に渡す。</summary>
    public DayTarget Target { get; }

    /// <summary>その日。</summary>
    public DateOnly Date => Target.Date;

    /// <summary>「この日を1日で見る」を出すか。</summary>
    public bool ShowsDay { get; }

    /// <summary>「この週を見る」を出すか。</summary>
    public bool ShowsWeek { get; }

    /// <summary>「この月を見る」を出すか。</summary>
    public bool ShowsMonth { get; }
}

/// <summary>「別のカレンダーへ移す」の子メニュー1件が起こす依頼。</summary>
/// <param name="EventId">移す予定の識別子。</param>
/// <param name="CalendarId">移し先のカレンダー。</param>
public sealed record EventMoveRequest(string EventId, string CalendarId);

/// <summary>「期限を変える」の子メニュー1件が起こす依頼。</summary>
/// <param name="TaskId">期限を変えるタスクの識別子。</param>
/// <param name="Due">新しい期限。null なら期限なし。</param>
public sealed record TaskDueRequest(string TaskId, DateOnly? Due);

/// <summary>「別のリストへ移す」の子メニュー1件が起こす依頼。</summary>
/// <param name="TaskId">移すタスクの識別子。</param>
/// <param name="TaskListId">移し先のタスクリスト。</param>
public sealed record TaskListMoveRequest(string TaskId, string TaskListId);
