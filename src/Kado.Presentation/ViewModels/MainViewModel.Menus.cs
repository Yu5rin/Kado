using Kado.Data.Models;
using Kado.Google.Mapping;
using Kado.Presentation.Editing;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Menus;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 右クリックメニュー（予定・タスク・日付と空き時間）の項目が起こす操作。
/// <para>
/// 画面ごとに行の型が違う（月のチップ・時間軸のブロック・右ペインの行・一覧の行など）ので、
/// 項目のコマンドは<b>右クリックされた行の中身をそのまま受け取り</b>、識別子を読み取る
/// （<see cref="EntryRefs"/>）。そのおかげで、どの画面のメニューも同じ書き方になる。
/// 中身は保存されている行を読み直す（表示用の複製を書き戻さない）。
/// </para>
/// <para>
/// <b>Google のカレンダーとタスクを壊さない</b>決まりに沿う。送れないものは編集させず（読み取り専用のカレンダー・
/// 向こうで変えられない予定は「詳細を見る」）、移すときは移す経路（編集画面でカレンダーを変えたのと同じ。
/// Google 側は <c>events.move</c> になる）を通し、複製は新規として作る（結び付きを持ち越さない）。
/// 全部、Undo できる編集（<c>Edits.cs</c>）として積む。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>「題名と日時をコピー」で文字を置く口。</summary>
    private readonly IClipboard _clipboard;

    // ------------------------------------------------------------------
    // コマンド（右クリックメニューの項目）
    // ------------------------------------------------------------------

    /// <summary>予定の「編集」（変えられない予定では「詳細を見る」）。引数は右クリックされた行。</summary>
    public RelayCommand<object?> EditEventEntryCommand { get; private set; } = null!;

    /// <summary>予定の「削除」。引数は右クリックされた行。</summary>
    public RelayCommand<object?> DeleteEventEntryCommand { get; private set; } = null!;

    /// <summary>予定の「複製」。同じ日・同じ時刻に、新しい予定を1件作る。</summary>
    public RelayCommand<object?> DuplicateEventCommand { get; private set; } = null!;

    /// <summary>予定の「題名と日時をコピー」。</summary>
    public RelayCommand<object?> CopyEventTextCommand { get; private set; } = null!;

    /// <summary>「別のカレンダーへ移す」の子メニューの1件。引数は <see cref="EventMoveRequest"/>。</summary>
    public RelayCommand<EventMoveRequest?> MoveEventToCalendarCommand { get; private set; } = null!;

    /// <summary>タスクの「編集」。引数は右クリックされた行。</summary>
    public RelayCommand<object?> EditTaskEntryCommand { get; private set; } = null!;

    /// <summary>タスクの「完了にする／完了を取り消す」。</summary>
    public RelayCommand<object?> ToggleTaskEntryDoneCommand { get; private set; } = null!;

    /// <summary>タスクの「削除」。</summary>
    public RelayCommand<object?> DeleteTaskEntryCommand { get; private set; } = null!;

    /// <summary>タスクの「複製」。</summary>
    public RelayCommand<object?> DuplicateTaskCommand { get; private set; } = null!;

    /// <summary>タスクの「題名をコピー」。</summary>
    public RelayCommand<object?> CopyTaskTitleCommand { get; private set; } = null!;

    /// <summary>「期限を変える」の子メニューの1件。引数は <see cref="TaskDueRequest"/>。</summary>
    public RelayCommand<TaskDueRequest?> SetTaskDueCommand { get; private set; } = null!;

    /// <summary>「別のリストへ移す」の子メニューの1件。引数は <see cref="TaskListMoveRequest"/>。</summary>
    public RelayCommand<TaskListMoveRequest?> MoveTaskToListCommand { get; private set; } = null!;

    /// <summary>「この日に予定を追加」。時間帯の上なら、その時刻から1時間の予定で編集画面を開く。</summary>
    public RelayCommand<DayTarget?> AddEventAtCommand { get; private set; } = null!;

    /// <summary>「この日にタスクを追加」。期限がその日の新しいタスクの編集画面を開く。</summary>
    public RelayCommand<DateOnly?> AddTaskOnCommand { get; private set; } = null!;

    /// <summary>「この週を見る」。その日を選んで、週ビューへ移る。</summary>
    public RelayCommand<DateOnly?> ShowWeekOfCommand { get; private set; } = null!;

    private void InitializeMenuCommands()
    {
        EditEventEntryCommand = new RelayCommand<object?>(entry => EditEventBy(EntryRefs.EventOf(entry)?.Id));
        DeleteEventEntryCommand = new RelayCommand<object?>(entry => DeleteEventBy(EntryRefs.EventOf(entry)?.Id));
        DuplicateEventCommand = new RelayCommand<object?>(entry => Guard(() => DuplicateEventCore(EntryRefs.EventOf(entry))));
        CopyEventTextCommand = new RelayCommand<object?>(entry => CopyEventText(EntryRefs.EventOf(entry)));
        MoveEventToCalendarCommand = new RelayCommand<EventMoveRequest?>(request => Guard(() => MoveEventToCalendarCore(request)));

        EditTaskEntryCommand = new RelayCommand<object?>(entry => EditTaskBy(EntryRefs.TaskOf(entry)));
        ToggleTaskEntryDoneCommand = new RelayCommand<object?>(entry => Guard(() => ToggleTaskEntryDoneCore(EntryRefs.TaskOf(entry))));
        DeleteTaskEntryCommand = new RelayCommand<object?>(entry => DeleteTaskBy(EntryRefs.TaskOf(entry)));
        DuplicateTaskCommand = new RelayCommand<object?>(entry => Guard(() => DuplicateTaskCore(EntryRefs.TaskOf(entry))));
        CopyTaskTitleCommand = new RelayCommand<object?>(entry => CopyTaskTitle(EntryRefs.TaskOf(entry)));
        SetTaskDueCommand = new RelayCommand<TaskDueRequest?>(request => Guard(() => SetTaskDueCore(request)));
        MoveTaskToListCommand = new RelayCommand<TaskListMoveRequest?>(request => Guard(() => MoveTaskToListCore(request)));

        AddEventAtCommand = new RelayCommand<DayTarget?>(AddEventAtTarget);
        AddTaskOnCommand = new RelayCommand<DateOnly?>(date =>
        {
            // 予定の追加（AddEventOnCommand）と同じく、その日を選んでから開く
            if (date is not { } day) return;

            SelectedDate = day;
            AddTask();
        });
        ShowWeekOfCommand = new RelayCommand<DateOnly?>(date => ShowOn(date, CalendarView.Week));

        // メニューの情報は、項目ごとに作られる（1回の右クリックで7〜8回）。データか左パネルの一覧が
        // 変わるまでは同じ答えなので、直近の1件を覚えておく（_menuEpoch が変わったら捨てる）
        _workspace.DataChanged += (_, _) => _menuEpoch++;
        SourceLists.PropertyChanged += (_, _) => _menuEpoch++;
        SourceLists.VisibilityChanged += (_, _) => _menuEpoch++;
        SourceLists.DefaultCalendarChanged += (_, _) => _menuEpoch++;
        SourceLists.DefaultTaskListChanged += (_, _) => _menuEpoch++;
    }

    /// <summary>メニューの情報を作り直すべき変化の回数。変わったら、覚えていた情報は使わない。</summary>
    private int _menuEpoch;

    private (string Id, int Epoch, EventMenuInfo Info)? _eventMenuMemo;

    private (string Id, int Epoch, DateOnly Today, DayOfWeek WeekStart, TaskMenuInfo Info)? _taskMenuMemo;

    // ------------------------------------------------------------------
    // 予定
    // ------------------------------------------------------------------

    /// <summary>
    /// 予定を編集させない理由。編集できるなら null。
    /// <para>
    /// 送れないカレンダー（読み取り専用・Google から外れた）の予定と、Google 側で変えられない予定
    /// （メールから起こされた予約・誕生日・勤務場所・他の人が主催する予定）。こちらで変えても
    /// 向こうへは伝わらず、画面と Google が食い違うだけになる。<see cref="UnsendableMessage"/> と
    /// <see cref="IsLocked"/> が、編集・ドラッグ・同期のどれでも使っている既存の判定。
    /// </para>
    /// <para>
    /// Kado では表せない繰り返し（RDATE など）の予定は、ここには含めない。題・場所・メモ・通知などは
    /// 直せて、日時と繰り返しだけを編集画面が止めている（<see cref="EventEditorViewModel.CanChangeSchedule"/>）。
    /// </para>
    /// </summary>
    private string? EditBlockReason(CalendarEvent stored) =>
        UnsendableMessage(stored) ?? (IsLocked(stored) ? LockedMessage : null);

    /// <summary>
    /// 予定の右クリックメニューを出し分ける情報。
    /// <para>押せない項目は灰色にして理由を持たせる（<see cref="EventMenuInfo"/>）。</para>
    /// </summary>
    /// <param name="entry">右クリックされた行（画面の <c>DataContext</c>）。</param>
    public EventMenuInfo EventMenuFor(object? entry)
    {
        if (EntryRefs.EventOf(entry) is not { } target || _workspace.Events.Find(target.Id) is not { } stored)
        {
            return EventMenuInfo.Unavailable;
        }

        if (_eventMenuMemo is { } memo && memo.Id == stored.Id && memo.Epoch == _menuEpoch) return memo.Info;

        var editReason = EditBlockReason(stored);

        // Google の events.move が受け付けない予定（他人が主催・種類が default 以外・繰り返しの1回だけの回）。
        // 子メニューを出したうえで、項目ごとに灰色にして理由を見せる
        var moveBlock = EventMapper.MoveBlockReason(stored);

        var choices = editReason is null
            ? MoveTargetCalendars(stored)
                .Select(c => new MenuChoice(
                    c.Name, MoveEventToCalendarCommand, new EventMoveRequest(stored.Id, c.Id),
                    c.SwatchColor, IsEnabled: moveBlock is null, ToolTip: moveBlock))
                .ToArray()
            : [];

        var moveDisabled = editReason
            ?? (choices.Length == 0 ? "移し先にできるカレンダーがありません（書き込めるカレンダーが他にありません）" : null);

        // 削除は、いま実際にできるものだけを押せるようにする（DeleteEventByCore と同じ判断）。
        // 向こうで変えられない予定でも、削除はできる。送れないカレンダーの予定は手元だけで消せる場合がある
        var deleteDisabled = UnsendableMessage(stored) is { } unsendable && !CanDeleteLocallyOnly(stored)
            ? unsendable
            : null;

        var duplicateDisabled = CalendarForCopy(stored) is null
            ? "予定を入れられるカレンダーがありません（読み取り専用のものしかありません）"
            : null;

        var info = new EventMenuInfo(editReason is null, duplicateDisabled, choices, moveDisabled, deleteDisabled);

        _eventMenuMemo = (stored.Id, _menuEpoch, info);
        return info;
    }

    /// <summary>
    /// 別のカレンダーへ移すときの移し先。左パネルの一覧と同じ並び。
    /// <para>
    /// 編集画面のカレンダー欄の候補（<see cref="CalendarChoicesFor"/>。読み取り専用・Google から外れたものは入らず、
    /// Google と結び付いた予定にはローカルのカレンダーも入らない）から、今のカレンダーを除いたもの。
    /// 実働日データの入れ先（Kado）は、取り込みのたびに入れ替わるので移し先にしない。
    /// </para>
    /// </summary>
    private IReadOnlyList<SourceListItemViewModel> MoveTargetCalendars(CalendarEvent stored)
    {
        var allowed = CalendarChoicesFor(stored).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        return SourceLists.Calendars
            .Where(c => allowed.Contains(c.Id) &&
                        !string.Equals(c.Id, stored.CalendarId, StringComparison.Ordinal) &&
                        !_workspace.IsWorkingDayCalendarId(c.Id))
            .ToArray();
    }

    /// <summary>
    /// 複製の入れ先。いまのカレンダーに書き込めるなら同じカレンダー、読み取り専用などで書き込めなければ
    /// 新しい予定の既定の入れ先。入れられるカレンダーが1つも無ければ null。
    /// </summary>
    private string? CalendarForCopy(CalendarEvent stored)
    {
        if (stored.CalendarId is { Length: > 0 } id &&
            SourceLists.Calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal)) is { CanReceive: true } &&
            !_workspace.IsWorkingDayCalendarId(id))
        {
            return id;
        }

        return QuickCalendarId;
    }

    private void DuplicateEventCore(EventEntry? target)
    {
        if (target is null || _workspace.Events.Find(target.Id) is not { } stored)
        {
            StatusMessage = "予定が見つかりませんでした";
            return;
        }

        if (CalendarForCopy(stored) is not { } calendarId)
        {
            StatusMessage = "予定を入れられるカレンダーがありません（読み取り専用のものしかありません）";
            return;
        }

        // 編集画面は開かない。作ったらすぐ元に戻せる（Ctrl＋Z）
        StatusMessage = _workspace.DuplicateEvent(stored.Id, calendarId, target.Date) is null
            ? "予定が見つかりませんでした"
            : "複製しました";
    }

    private void CopyEventText(EventEntry? target)
    {
        if (target is null || _workspace.Events.Find(target.Id) is not { } stored)
        {
            StatusMessage = "予定が見つかりませんでした";
            return;
        }

        CopyToClipboard(EntryText.ForEvent(stored, target.Date));
    }

    private void CopyToClipboard(string text) =>
        StatusMessage = _clipboard.TrySetText(text) ? "コピーしました" : "コピーできませんでした（クリップボードを他のアプリが使っています）";

    private void MoveEventToCalendarCore(EventMoveRequest? request)
    {
        if (request is null || _workspace.Events.Find(request.EventId) is not { } stored)
        {
            StatusMessage = "予定が見つかりませんでした";
            return;
        }

        // 子メニューは、押せない項目を灰色にしている。呼ばれ方によらず、ここでも同じ理由で止める
        if (EditBlockReason(stored) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        if (EventMapper.MoveBlockReason(stored) is { } cannotMove)
        {
            StatusMessage = cannotMove;
            return;
        }

        var target = MoveTargetCalendars(stored)
            .FirstOrDefault(c => string.Equals(c.Id, request.CalendarId, StringComparison.Ordinal));

        if (target is null)
        {
            StatusMessage = "そのカレンダーには移せません";
            return;
        }

        StatusMessage = _workspace.MoveEventToCalendar(stored.Id, target.Id)
            ? $"「{target.Name}」へ移しました"
            : "予定が見つかりませんでした";
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    /// <summary>
    /// タスクの右クリックメニューを出し分ける情報。
    /// </summary>
    /// <param name="entry">右クリックされた行（画面の <c>DataContext</c>）。</param>
    public TaskMenuInfo TaskMenuFor(object? entry)
    {
        if (EntryRefs.TaskOf(entry) is not { } id || _workspace.Tasks.Find(id) is not { } stored)
        {
            return TaskMenuInfo.Unavailable;
        }

        if (_taskMenuMemo is { } memo &&
            memo.Id == stored.Id && memo.Epoch == _menuEpoch && memo.Today == _today && memo.WeekStart == _weekStart)
        {
            return memo.Info;
        }

        // 期限。稼働日は、稼働日データ＋データの無い期間は土日・祝日を除く（タスクの繰り返しと同じ決まり）
        var due = TaskDuePresets.For(_today, _weekStart, _workspace.Workdays.IsWorkday)
            .Select(preset => DueChoice(stored, preset))
            .ToArray();

        // リスト。サブタスクと、サブタスクを持つタスクは、Google がリストをまたいで移させない
        var block = TaskMapper.MoveBlockReason(stored, TaskMapper.HasChildren(stored, _workspace.Tasks.All()));

        var lists = MoveTargetTaskLists(stored)
            .Select(l => new MenuChoice(
                l.Name, MoveTaskToListCommand, new TaskListMoveRequest(stored.Id, l.Id),
                l.SwatchColor, IsEnabled: block is null, ToolTip: block))
            .ToArray();

        var info = new TaskMenuInfo(
            stored.IsDone ? "完了を取り消す" : "完了にする",
            due,
            lists,
            lists.Length == 0 ? "移し先にできるタスクリストがありません" : null);

        _taskMenuMemo = (stored.Id, _menuEpoch, _today, _weekStart, info);
        return info;
    }

    private MenuChoice DueChoice(TaskItem stored, TaskDuePreset preset)
    {
        var parameter = new TaskDueRequest(stored.Id, preset.Due);

        if (preset.Due is not { } date)
        {
            // 期限なし。もう期限が無いなら灰色。見つからなかった日付の項目は、灰色にして理由を出す
            return preset.Kind == TaskDueKind.None
                ? new MenuChoice(
                    preset.Label, SetTaskDueCommand, parameter,
                    IsEnabled: stored.Due is not null,
                    ToolTip: stored.Due is null ? "期限はありません" : stored.IsRepeating ? "繰り返しも外れます" : null)
                : new MenuChoice(
                    preset.Label, SetTaskDueCommand, parameter, IsEnabled: false, ToolTip: "稼働日が見つかりません");
        }

        var current = stored.Due == date;

        return new MenuChoice(
            preset.Label, SetTaskDueCommand, parameter,
            IsEnabled: !current,
            ToolTip: current ? "いまの期限です" : EntryText.Day(date));
    }

    /// <summary>別のリストへ移すときの移し先。左パネルの一覧と同じ並び。今のリストは含まない。</summary>
    private IReadOnlyList<SourceListItemViewModel> MoveTargetTaskLists(TaskItem stored)
    {
        var allowed = TaskListChoicesFor(stored).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        return SourceLists.TaskLists
            .Where(l => allowed.Contains(l.Id) && !string.Equals(l.Id, stored.TaskListId, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// タスクの複製の入れ先。いまのリストに入れられるなら同じリスト、そうでなければ新しいタスクの既定の入れ先。
    /// </summary>
    private string? TaskListForCopy(TaskItem stored)
    {
        if (stored.TaskListId is { Length: > 0 } id &&
            SourceLists.TaskLists.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal)) is { CanReceive: true })
        {
            return id;
        }

        return SourceLists.DefaultTaskList?.Id;
    }

    private void ToggleTaskEntryDoneCore(string? id)
    {
        if (id is not { Length: > 0 } || _workspace.ToggleTask(id, _today, _weekStart) is not { } result) return;

        StatusMessage = ToggledMessage(result);
    }

    private void DuplicateTaskCore(string? id)
    {
        if (id is not { Length: > 0 } || _workspace.Tasks.Find(id) is not { } stored)
        {
            StatusMessage = "タスクが見つかりませんでした";
            return;
        }

        StatusMessage = _workspace.DuplicateTask(stored.Id, TaskListForCopy(stored)) is null
            ? "タスクが見つかりませんでした"
            : "複製しました";
    }

    private void CopyTaskTitle(string? id)
    {
        if (id is not { Length: > 0 } || _workspace.Tasks.Find(id) is not { } stored)
        {
            StatusMessage = "タスクが見つかりませんでした";
            return;
        }

        CopyToClipboard(EntryText.ForTask(stored));
    }

    private void SetTaskDueCore(TaskDueRequest? request)
    {
        if (request is null || _workspace.Tasks.Find(request.TaskId) is not { } stored)
        {
            StatusMessage = "タスクが見つかりませんでした";
            return;
        }

        if (stored.Due == request.Due) return;

        var hadRepeat = stored.IsRepeating;

        if (!_workspace.SetTaskDue(stored.Id, request.Due))
        {
            StatusMessage = "タスクが見つかりませんでした";
            return;
        }

        StatusMessage = request.Due is { } due
            ? $"期限を {EntryText.Day(due)} に変えました"
            : hadRepeat ? "期限を外しました（繰り返しも外れました）" : "期限を外しました";
    }

    private void MoveTaskToListCore(TaskListMoveRequest? request)
    {
        if (request is null || _workspace.Tasks.Find(request.TaskId) is not { } stored)
        {
            StatusMessage = "タスクが見つかりませんでした";
            return;
        }

        // 子メニューは、押せない項目を灰色にしている。呼ばれ方によらず、ここでも同じ理由で止める
        if (TaskMapper.MoveBlockReason(stored, TaskMapper.HasChildren(stored, _workspace.Tasks.All())) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        var target = MoveTargetTaskLists(stored)
            .FirstOrDefault(l => string.Equals(l.Id, request.TaskListId, StringComparison.Ordinal));

        if (target is null)
        {
            StatusMessage = "そのリストには移せません";
            return;
        }

        StatusMessage = _workspace.MoveTaskToList(stored.Id, target.Id)
            ? $"「{target.Name}」へ移しました"
            : "タスクが見つかりませんでした";
    }

    // ------------------------------------------------------------------
    // 日付・空き時間
    // ------------------------------------------------------------------

    /// <summary>
    /// 日付・空き時間の右クリックメニューを出し分ける情報。
    /// </summary>
    /// <param name="entry">右クリックされた場所の中身（画面の <c>DataContext</c>）。</param>
    /// <param name="time">時間帯の上で右クリックしたときの、15分に丸めた時刻。</param>
    /// <returns>日付を読めない場所のときは null。</returns>
    public DayMenuInfo? DayMenuFor(object? entry, TimeOnly? time = null)
    {
        if (DayTarget.From(entry, time) is not { } target) return null;

        return new DayMenuInfo(
            target,
            showsDay: _currentView != CalendarView.Day,
            showsWeek: _currentView != CalendarView.Week,
            showsMonth: _currentView != CalendarView.Month);
    }

    private void AddEventAtTarget(DayTarget? target)
    {
        if (target is null) return;

        // 時間帯の上なら、その時刻から（長さは既定の1時間）。そうでなければ、ふつうの「予定を追加」
        if (target.Time is { } time)
        {
            AddEventAt(target.Date, time);
            return;
        }

        AddEventOnCommand.Execute(target.Date);
    }
}
