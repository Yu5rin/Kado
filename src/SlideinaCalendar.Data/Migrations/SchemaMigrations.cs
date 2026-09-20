namespace SlideinaCalendar.Data.Migrations;

/// <summary>スキーマの定義一覧。</summary>
public static class SchemaMigrations
{
    /// <summary>
    /// 初版。予定・タスク・作業時間ブロック・実働日・マイルストーン・設定・
    /// 同期状態・tombstone を作る。
    /// </summary>
    private const string V1 = """
        -- 予定。タスクとは別テーブルで持つ（要件書 3.1）
        CREATE TABLE events (
            id              TEXT    NOT NULL PRIMARY KEY,
            title           TEXT    NOT NULL,
            date            TEXT    NOT NULL,   -- yyyy-MM-dd
            end_date        TEXT,               -- 複数日予定の終了日
            start_time      TEXT,               -- HH:mm。NULL なら終日
            end_time        TEXT,
            location        TEXT,
            note            TEXT,
            color           TEXT,
            calendar_id     TEXT,
            recurrence      TEXT,               -- FREQ=... 形式。単発なら NULL
            google_event_id TEXT,
            google_updated  TEXT,
            source          TEXT,
            updated_at      INTEGER NOT NULL    -- epoch ミリ秒
        );

        -- 日付は範囲検索の主役なので必ず張る
        CREATE INDEX ix_events_date ON events (date);
        -- 繰り返し予定は日付で絞り込めないため、別に引けるようにする
        CREATE INDEX ix_events_recurrence ON events (recurrence) WHERE recurrence IS NOT NULL;
        -- Google からの差分を突き合わせるのに使う
        CREATE UNIQUE INDEX ux_events_google ON events (google_event_id) WHERE google_event_id IS NOT NULL;

        -- タスク。期限だけある状態を持てる必要があるため予定とは別（要件書 3.1）
        CREATE TABLE tasks (
            id                  TEXT    NOT NULL PRIMARY KEY,
            title               TEXT    NOT NULL,
            due                 TEXT,           -- yyyy-MM-dd。未定なら NULL
            is_done             INTEGER NOT NULL DEFAULT 0,
            note                TEXT,
            task_list_id        TEXT,
            google_task_id      TEXT,
            google_task_list_id TEXT,
            google_updated      TEXT,
            source              TEXT,
            updated_at          INTEGER NOT NULL
        );

        CREATE INDEX ix_tasks_due ON tasks (due) WHERE due IS NOT NULL;
        CREATE UNIQUE INDEX ux_tasks_google ON tasks (google_task_id) WHERE google_task_id IS NOT NULL;

        -- 作業時間ブロック。タスク側の属性であり、予定には変換しない（要件書 5.4）
        CREATE TABLE work_blocks (
            id               TEXT    NOT NULL PRIMARY KEY,
            task_id          TEXT    NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
            date             TEXT    NOT NULL,
            start_time       TEXT    NOT NULL,
            duration_minutes INTEGER NOT NULL
        );

        CREATE INDEX ix_work_blocks_date ON work_blocks (date);
        CREATE INDEX ix_work_blocks_task ON work_blocks (task_id);

        -- 実働日。土日祝を除いた日ではなく「登録された日の集合」として持つ
        CREATE TABLE working_days (
            date TEXT NOT NULL PRIMARY KEY
        );

        -- 実働日データの登録範囲。範囲外は「データなし」として暦日にフォールバックする。
        -- 稼働日とマイルストーンで期間が異なるため、種別ごとに持つ（要件書 4.1）
        CREATE TABLE data_ranges (
            kind       TEXT NOT NULL PRIMARY KEY,   -- 'working_day' / 'milestone'
            start_date TEXT NOT NULL,
            end_date   TEXT NOT NULL
        );

        -- マイルストーン。読み取り専用で Google にも同期しない（要件書 4.2）。
        -- 種類名は取り込んだ文字列をそのまま使い、固定4種で決め打ちしない
        CREATE TABLE milestones (
            date           TEXT NOT NULL,
            name           TEXT NOT NULL,
            source_version TEXT,
            PRIMARY KEY (date, name)
        );

        CREATE INDEX ix_milestones_date ON milestones (date);

        -- 設定。値は文字列で持ち、構造のあるものは JSON にする
        CREATE TABLE settings (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- 同期状態（syncToken など）。予定とタスクで独立した経路を持つため種別で分ける
        CREATE TABLE sync_state (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- 削除の伝播に使う。消したことを覚えていないと、次の同期で復活してしまう
        CREATE TABLE tombstones (
            id         TEXT    NOT NULL,
            kind       TEXT    NOT NULL,   -- 'event' / 'task'
            google_id  TEXT,
            deleted_at INTEGER NOT NULL,
            PRIMARY KEY (id, kind)
        );

        CREATE INDEX ix_tombstones_deleted_at ON tombstones (deleted_at);
        """;

    /// <summary>
    /// 予定に URL を足す。
    /// <para>
    /// Google Calendar のイベントは <c>source.url</c> を持つ。資料や図面の置き場所を
    /// 説明欄に書くと、リンクなのか本文なのか分からなくなる。
    /// </para>
    /// </summary>
    private const string V2 = """
        ALTER TABLE events ADD COLUMN url TEXT;
        """;


    /// <summary>
    /// 同期の受け皿を用意する。
    /// <para>
    /// 書き戻しは <c>patch</c> で行う。指定しなかった項目は Google 側でそのまま
    /// 残るので、ゲストや通知を列で抱えなくても消えない。ここで足すのは
    /// <b>差分の判定に要るもの</b>と、<b>カレンダー一覧そのもの</b>。
    /// </para>
    /// </summary>
    private const string V3 = """
        -- 最後に Google から受け取った姿。キーをソートした JSON を入れる。
        -- 並び順の違いで「変わった」と誤判定するのを防ぐ（要件書 6.3）
        ALTER TABLE events ADD COLUMN google_raw TEXT;

        -- confirmed / tentative / cancelled。cancelled は削除として扱う
        ALTER TABLE events ADD COLUMN status TEXT;

        -- source は url と title の対。url だけ持っていると書き戻せない
        ALTER TABLE events ADD COLUMN source_title TEXT;

        ALTER TABLE tasks ADD COLUMN google_raw TEXT;

        -- 完了した日時。完了したことだけでは、どちらが新しいか判定できない
        ALTER TABLE tasks ADD COLUMN completed_at INTEGER;

        -- サブタスクの親と、同じ階層での並び順。どちらも Google 側では
        -- move でしか変えられないので、読んで持っておく
        ALTER TABLE tasks ADD COLUMN parent_id TEXT;
        ALTER TABLE tasks ADD COLUMN position TEXT;

        CREATE INDEX ix_tasks_parent ON tasks (parent_id);

        -- カレンダー一覧。これまでは予定の calendar_id から名前を拾い、
        -- 色は名前から作っていた。取り込めば本物の名前と色になる（要件書 6.3）
        CREATE TABLE calendars (
            id               TEXT    NOT NULL PRIMARY KEY,
            summary          TEXT    NOT NULL,
            summary_override TEXT,               -- 利用者が付け替えた表示名
            background_color TEXT,               -- #rrggbb
            foreground_color TEXT,
            is_primary       INTEGER NOT NULL DEFAULT 0,
            is_visible       INTEGER NOT NULL DEFAULT 1,   -- 左パネルのチェック
            sort_order       INTEGER NOT NULL DEFAULT 0,
            google_raw       TEXT,
            updated_at       INTEGER NOT NULL
        );

        -- タスクリスト。カレンダーとは独立した同期経路なので表も分ける
        CREATE TABLE task_lists (
            id         TEXT    NOT NULL PRIMARY KEY,
            title      TEXT    NOT NULL,
            is_visible INTEGER NOT NULL DEFAULT 1,
            sort_order INTEGER NOT NULL DEFAULT 0,
            google_raw TEXT,
            updated_at INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// 通知するかどうかを、予定ごととカレンダーごとに持つ。
    /// <para>
    /// 予定の <c>notify</c> は3つの状態を取る。1 なら知らせる、0 なら知らせない、
    /// NULL なら「カレンダーの決まりに従う」。全部に印を付けさせないための NULL。
    /// </para>
    /// </summary>
    private const string V4 = """
        ALTER TABLE events ADD COLUMN notify INTEGER;
        ALTER TABLE calendars ADD COLUMN notify_default INTEGER NOT NULL DEFAULT 1;
        """;

    /// <summary>適用順に並んだスキーマ定義。</summary>
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "予定・タスク・実働日・マイルストーン・設定・同期状態の初版", V1),
        new(2, "予定に URL を足す", V2),
        new(3, "同期の受け皿（差分判定用の生データ、カレンダー一覧、タスクリスト）", V3),
        new(4, "通知するかどうかを予定ごと・カレンダーごとに持つ", V4),
    ];

    /// <summary>このコードが期待する最新の版。</summary>
    public static int LatestVersion => All.Max(m => m.Version);
}
