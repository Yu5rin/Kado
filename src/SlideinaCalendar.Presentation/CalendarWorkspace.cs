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
        Settings = new SettingsRepository(connection);
        Schedule = new ScheduleQuery(Events, Tasks);

        _workingDays = WorkingDayStore.Load();
        WorkingDayMath = new WorkingDayMath(_workingDays);
        DueFormatter = new DueDateFormatter(WorkingDayMath);
    }

    public EventRepository Events { get; }
    public TaskRepository Tasks { get; }
    public WorkingDayRepository WorkingDayStore { get; }
    public SettingsRepository Settings { get; }
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

        Run(new DeleteEventEdit(Events, value));
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

        Run(new DeleteTaskEdit(Tasks, value, blocks));
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
