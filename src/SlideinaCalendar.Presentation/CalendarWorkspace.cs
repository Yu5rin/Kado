using Microsoft.Data.Sqlite;
using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Import;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation;

/// <summary>
/// データ操作の入口。
/// <para>
/// ViewModel がリポジトリを直接触ると、Undo を通さない変更が混ざりうる。編集は必ず
/// ここを通し、<see cref="Undo"/> に履歴を積む。実働日の判定に要る Core の
/// オブジェクトもここで組み立てて配る。
/// </para>
/// </summary>
public sealed class CalendarWorkspace
{
    private readonly SqliteConnection _connection;

    private WorkingDayCalendar _workingDays;

    public CalendarWorkspace(SqliteConnection connection, IHolidaySource? holidays = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        Holidays = holidays ?? EmptyHolidaySource.Instance;

        Events = new EventRepository(connection);
        Tasks = new TaskRepository(connection);
        WorkingDayStore = new WorkingDayRepository(connection);
        Sources = new SourceRepository(connection);
        Settings = new SettingsRepository(connection);
        Tombstones = new TombstoneRepository(connection);
        Schedule = new ScheduleQuery(Events, Tasks);

        EnsureSources();

        _workingDays = WorkingDayStore.Load();
        WorkingDayMath = new WorkingDayMath(_workingDays);
        DueFormatter = new DueDateFormatter(WorkingDayMath);
    }

    public EventRepository Events { get; }
    public TaskRepository Tasks { get; }
    public WorkingDayRepository WorkingDayStore { get; }

    /// <summary>カレンダーとタスクリスト。同期を始めるまでは空のことがある。</summary>
    public SourceRepository Sources { get; }
    public SettingsRepository Settings { get; }

    /// <summary>
    /// 消したことの記録。
    /// <para>残さないと、次の同期で消したものが復活する。伝え終わったら消える。</para>
    /// </summary>
    public TombstoneRepository Tombstones { get; }

    public ScheduleQuery Schedule { get; }

    /// <summary>元に戻す・やり直しの履歴。</summary>
    public UndoStack Undo { get; } = new();

    /// <summary>祝日の名前を引く。取り込むまでは何も返さない実装が入る。</summary>
    public IHolidaySource Holidays { get; }

    /// <summary>実働日の判定に使うカレンダー。</summary>
    public WorkingDayCalendar WorkingDays => _workingDays;

    /// <summary>実働日での日数計算。</summary>
    public WorkingDayMath WorkingDayMath { get; private set; }

    /// <summary>期限の表示文字列を組み立てる。</summary>
    public DueDateFormatter DueFormatter { get; private set; }

    /// <summary>データが変わったときに呼ばれる。ビューはこれを見て引き直す。</summary>
    public event EventHandler? DataChanged;

    /// <summary>実働日データを読み直す。Excel を取り込んだあとなどに呼ぶ。</summary>
    public void ReloadWorkingDays()
    {
        _workingDays = WorkingDayStore.Load();
        WorkingDayMath = new WorkingDayMath(_workingDays);
        DueFormatter = new DueDateFormatter(WorkingDayMath);
        NotifyChanged();
    }

    // ------------------------------------------------------------------
    // カレンダーとタスクリスト
    //
    // Google に繋がなくても、このアプリだけで分類を作れる。仕事と私用を
    // 分けて入れたい、というのは連携の有無に関わらず要る
    // ------------------------------------------------------------------

    /// <summary>既定のカレンダー名。何も無いときに作る。</summary>
    public const string DefaultCalendarName = "マイカレンダー";

    /// <summary>既定のタスクリスト名。</summary>
    public const string DefaultTaskListName = "マイタスク";

    /// <summary>カレンダーを作る。</summary>
    /// <returns>作ったカレンダー。</returns>
    public CalendarSource CreateCalendar(string name, string? color = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var value = new CalendarSource
        {
            // Google 側の ID と衝突しないよう、こちらで作ったものは印を付ける
            Id = $"local:{Guid.NewGuid():N}"[..21],
            Summary = name.Trim(),
            BackgroundColor = color ?? CalendarPalette.NextColor(
                Sources.Calendars().Select(c => c.BackgroundColor)),
            SortOrder = Sources.NextCalendarOrder(),
            UpdatedAt = DateTimeOffset.Now,
        };

        Sources.Upsert(value);
        NotifyChanged();

        return value;
    }

    /// <summary>タスクリストを作る。</summary>
    public TaskListSource CreateTaskList(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var value = new TaskListSource
        {
            Id = $"local:{Guid.NewGuid():N}"[..21],
            Title = title.Trim(),
            SortOrder = Sources.NextTaskListOrder(),
            UpdatedAt = DateTimeOffset.Now,
        };

        Sources.Upsert(value);
        NotifyChanged();

        return value;
    }

    /// <summary>カレンダーの名前と色を変える。</summary>
    public bool UpdateCalendar(string id, string name, string? color)
    {
        if (!Sources.UpdateCalendar(id, name, color)) return false;

        NotifyChanged();
        return true;
    }

    /// <summary>タスクリストの名前を変える。</summary>
    public bool UpdateTaskList(string id, string title)
    {
        if (!Sources.UpdateTaskList(id, title)) return false;

        NotifyChanged();
        return true;
    }

    /// <summary>
    /// カレンダーを消す。中の予定は別のカレンダーへ移す。
    /// <para>分類を消したかっただけなのに中身まで消えるのは行き過ぎ。</para>
    /// </summary>
    /// <returns>移した予定の件数。最後の1つは消せないので、そのときは null。</returns>
    public int? DeleteCalendar(string id)
    {
        var remaining = Sources.Calendars()
            .Where(c => !string.Equals(c.Id, id, StringComparison.Ordinal))
            .ToArray();

        // 入れ先が無くなると、予定の所属が消えて分類できなくなる
        if (remaining.Length == 0) return null;

        var moved = Sources.DeleteCalendar(id, remaining[0].Id);
        NotifyChanged();

        return moved;
    }

    /// <summary>タスクリストを消す。中のタスクは別のリストへ移す。</summary>
    public int? DeleteTaskList(string id)
    {
        var remaining = Sources.TaskLists()
            .Where(t => !string.Equals(t.Id, id, StringComparison.Ordinal))
            .ToArray();

        if (remaining.Length == 0) return null;

        var moved = Sources.DeleteTaskList(id, remaining[0].Id);
        NotifyChanged();

        return moved;
    }

    /// <summary>
    /// 予定やタスクが指している所属を、一覧の表に起こす。
    /// <para>
    /// 起動時と、取り込みのあとに呼ぶ。足りないぶんだけ足すので何度呼んでもよい。
    /// これまでは予定の所属 ID から後付けで拾っていた。表に載せてしまえば、名前も色も
    /// 変えられるし、予定が1件も無くても分類を先に作れる。
    /// </para>
    /// </summary>
    public void EnsureSources()
    {
        var calendars = Sources.Calendars();
        var known = calendars.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var order = Sources.NextCalendarOrder();

        foreach (var id in Events.CalendarIds().Where(id => !known.Contains(id)))
        {
            Sources.Upsert(new CalendarSource
            {
                Id = id,
                Summary = id,
                BackgroundColor = CalendarPalette.ColorFor(id),
                IsPrimary = calendars.Count == 0 && order == 0,
                SortOrder = order++,
                UpdatedAt = DateTimeOffset.Now,
            });
        }

        // 1つも無いと予定の入れ先が決まらない
        if (order == 0 && calendars.Count == 0)
        {
            Sources.Upsert(new CalendarSource
            {
                Id = DefaultCalendarName,
                Summary = DefaultCalendarName,
                BackgroundColor = CalendarPalette.ColorFor(DefaultCalendarName),
                IsPrimary = true,
                UpdatedAt = DateTimeOffset.Now,
            });
        }

        var taskLists = Sources.TaskLists();
        var knownLists = taskLists.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var listOrder = Sources.NextTaskListOrder();

        foreach (var id in Tasks.TaskListIds().Where(id => !knownLists.Contains(id)))
        {
            Sources.Upsert(new TaskListSource
            {
                Id = id,
                Title = id,
                SortOrder = listOrder++,
                UpdatedAt = DateTimeOffset.Now,
            });
        }

        if (listOrder == 0 && taskLists.Count == 0)
        {
            Sources.Upsert(new TaskListSource
            {
                Id = DefaultTaskListName,
                Title = DefaultTaskListName,
                UpdatedAt = DateTimeOffset.Now,
            });
        }
    }

    // ------------------------------------------------------------------
    // 取り込み
    //
    // どちらも Undo には積まない。まとめて書き込むので、1手で戻せる単位に
    // ならない。戻したいときはバックアップから復元する
    // ------------------------------------------------------------------

    /// <summary>
    /// 配布の実働日ファイル（Excel）を取り込む。
    /// <para>
    /// 期間単位で置き換える。古いファイルを誤って読み込んでも、その期間の外は
    /// 消えない（要件書 4.1）。
    /// </para>
    /// </summary>
    public ImportResult ImportWorkingDays(Stream xlsx)
    {
        ArgumentNullException.ThrowIfNull(xlsx);

        var result = new WorkdayFileImporter().Import(xlsx);
        WorkingDayStore.Apply(result);

        ReloadWorkingDays();
        return result;
    }

    /// <summary>旧 inaCalendar のバックアップ（JSON）を取り込む。</summary>
    public LegacyImportResult ImportLegacyBackup(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var result = new LegacyBackupMigrator(_connection).Migrate(json);

        // 旧データが持ち込んだ所属を一覧に起こす。載せないと左パネルに出ない
        EnsureSources();

        ReloadWorkingDays();
        return result;
    }

    // ------------------------------------------------------------------
    // 編集。すべて Undo を通す
    // ------------------------------------------------------------------

    /// <summary>予定を追加する。</summary>
    public void AddEvent(CalendarEvent value) => Run(new AddEventEdit(Events, value));

    /// <summary>
    /// 予定を書き換える。
    /// <para>書き換え前の姿は保存されている内容から取る。呼び出し側が渡した古い値は信用しない。</para>
    /// </summary>
    /// <returns>対象が見つかって書き換えたら true。</returns>
    public bool UpdateEvent(CalendarEvent after)
    {
        ArgumentNullException.ThrowIfNull(after);

        if (Events.Find(after.Id) is not { } before) return false;

        Run(new UpdateEventEdit(Events, before, after));
        return true;
    }

    /// <summary>予定を削除する。</summary>
    /// <returns>対象が見つかって削除したら true。</returns>
    public bool DeleteEvent(string id)
    {
        if (Events.Find(id) is not { } value) return false;

        Run(new DeleteEventEdit(Events, value, Tombstones));
        return true;
    }

    /// <summary>タスクを追加する。</summary>
    public void AddTask(TaskItem value) => Run(new AddTaskEdit(Tasks, value));

    /// <summary>タスクを書き換える。</summary>
    /// <returns>対象が見つかって書き換えたら true。</returns>
    public bool UpdateTask(TaskItem after)
    {
        ArgumentNullException.ThrowIfNull(after);

        if (Tasks.Find(after.Id) is not { } before) return false;

        Run(new UpdateTaskEdit(Tasks, before, after));
        return true;
    }

    /// <summary>タスクの完了を切り替える。</summary>
    /// <returns>対象が見つかって切り替えたら true。</returns>
    public bool ToggleTaskDone(string id)
    {
        if (Tasks.Find(id) is not { } before) return false;

        var after = before with { IsDone = !before.IsDone, UpdatedAt = DateTimeOffset.Now };
        Run(new UpdateTaskEdit(Tasks, before, after));
        return true;
    }

    /// <summary>タスクを削除する。作業時間ブロックも一緒に消える。</summary>
    /// <returns>対象が見つかって削除したら true。</returns>
    public bool DeleteTask(string id)
    {
        if (Tasks.Find(id) is not { } value) return false;

        // 連鎖で消える作業時間ブロックを控えておかないと、元に戻したときに失われる
        var blocks = Tasks.BlocksOf(id);

        Run(new DeleteTaskEdit(Tasks, value, blocks, Tombstones));
        return true;
    }

    // ------------------------------------------------------------------
    // 元に戻す・やり直し
    // ------------------------------------------------------------------

    /// <summary>直前の編集を元に戻す。</summary>
    /// <returns>戻した編集の説明。戻すものが無ければ null。</returns>
    public string? UndoLast()
    {
        var description = Undo.Undo();
        if (description is not null) NotifyChanged();
        return description;
    }

    /// <summary>元に戻した編集をやり直す。</summary>
    /// <returns>やり直した編集の説明。やり直すものが無ければ null。</returns>
    public string? RedoLast()
    {
        var description = Undo.Redo();
        if (description is not null) NotifyChanged();
        return description;
    }

    private void Run(IUndoableEdit edit)
    {
        Undo.Execute(edit);
        NotifyChanged();
    }

    private void NotifyChanged() => DataChanged?.Invoke(this, EventArgs.Empty);
}
