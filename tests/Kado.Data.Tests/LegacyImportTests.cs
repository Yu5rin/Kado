using Kado.Core.Recurrence;
using Kado.Data.Import;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// 旧 inaCalendar のバックアップからの移行。
/// <para>
/// 実データから匿名化したサンプルを使う。件数や境界のケースは実データのままなので、
/// 形式の読み違いがあればここで落ちる。
/// </para>
/// </summary>
public class LegacyImportTests
{
    private static LegacyImportResult Read()
    {
        using var stream = TestDatabase.OpenLegacyBackup();
        return LegacyBackupMigrator.Read(stream);
    }

    // ------------------------------------------------------------------
    // 読み込み
    // ------------------------------------------------------------------

    [Fact]
    public void バックアップを読み込める()
    {
        var result = Read();

        Assert.Equal("inaCalendar", result.SourceApp);
        Assert.Equal(3, result.SourceVersion);
        Assert.NotNull(result.ExportedAt);
    }

    [Fact]
    public void 予定とタスクに分かれる()
    {
        var result = Read();

        // サンプルは userEvents が 234 件。うち ToDo フラグつきの 13 件がタスクへ移り、
        // 別枠の recurringEvents 1 件が予定に加わる
        Assert.Equal(13, result.Tasks.Count);
        Assert.Equal(234 - 13 + 1, result.Events.Count);
    }

    [Fact]
    public void ToDoフラグつきの予定はタスクへ変換される()
    {
        var result = Read();

        Assert.Equal(13, result.ConvertedTaskCount);
        Assert.All(result.Tasks, t =>
        {
            Assert.Equal(LegacyBackupImporter.ConvertedTaskSource, t.Source);
            // ToDo は日付キーの下にあったので、必ず期限が決まる
            Assert.NotNull(t.Due);
        });
    }

    [Fact]
    public void 完了状態が引き継がれる()
    {
        var result = Read();

        // 実データの内訳は done=true が 11 件、未設定（＝未完了）が 2 件
        Assert.Equal(11, result.Tasks.Count(t => t.IsDone));
        Assert.Equal(2, result.Tasks.Count(t => !t.IsDone));
    }

    [Fact]
    public void GoogleTasksとのリンクが引き継がれる()
    {
        var result = Read();

        // 11 件は既に Google Tasks と紐づいている。切れると同期で重複を生む
        Assert.Equal(11, result.Tasks.Count(t => t.GoogleTaskId is not null));
        Assert.All(result.Tasks.Where(t => t.GoogleTaskId is not null),
            t => Assert.NotNull(t.GoogleTaskListId));
    }

    [Fact]
    public void 複数日予定の期間が保たれる()
    {
        var result = Read();
        var multi = result.Events.Where(e => e.EndDate is not null).ToArray();

        Assert.Equal(22, multi.Length);
        Assert.All(multi, e => Assert.True(e.EndDate >= e.Date));
    }

    [Fact]
    public void 終日と時刻ありが区別される()
    {
        var result = Read();

        Assert.Contains(result.Events, e => e.IsAllDay);
        Assert.Contains(result.Events, e => !e.IsAllDay);

        // 時刻があるなら開始と終了がそろっている
        Assert.All(result.Events.Where(e => !e.IsAllDay), e => Assert.NotNull(e.EndTime));
    }

    [Fact]
    public void 遠い未来の予定も読める()
    {
        var result = Read();

        // 2099 年の予定が実データに混じっている。日付処理が破綻しないこと
        Assert.Contains(result.Events, e => e.Date.Year == 2099);
    }

    [Fact]
    public void Google未同期のものも取り込む()
    {
        var result = Read();

        // 実データで gcalId を持たない 13 件は、すべて ToDo フラグつきだった。
        // つまり Google Calendar と紐づいていない予定は残らず、タスク側へ移っている
        Assert.DoesNotContain(result.Events, e => e.GoogleEventId is null);

        // タスク 13 件のうち 2 件は Google Tasks にも未同期。ローカルだけの存在なので
        // 落とすとデータが消える
        Assert.Equal(2, result.Tasks.Count(t => t.GoogleTaskId is null));
    }

    // ------------------------------------------------------------------
    // 繰り返しと除外日
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返し予定が除外日つきで変換される()
    {
        var result = Read();
        var recurring = Assert.Single(result.Events, e => e.IsRecurring);

        var rule = RecurrenceRule.Parse(recurring.Recurrence!);

        Assert.Equal("YEARLY", rule.Frequency);
        Assert.Equal(2, rule.ExceptDates.Count);
        Assert.Contains(new DateOnly(2026, 4, 21), rule.ExceptDates);
        Assert.Contains(new DateOnly(2027, 4, 21), rule.ExceptDates);

        // 除外された年は該当せず、除外されていない年は該当する
        Assert.False(rule.Matches(new DateOnly(2027, 4, 21), recurring.Date));
        Assert.True(rule.Matches(new DateOnly(2028, 4, 21), recurring.Date));
    }

    // ------------------------------------------------------------------
    // 実働日と設定
    // ------------------------------------------------------------------

    [Fact]
    public void 実働日が実ファイルと一致する()
    {
        var result = Read();

        // Excel から取り込んだものと同じ 753 件・同じ期間であること
        Assert.Equal(753, result.WorkingDays.Count);
        Assert.Equal(new DateOnly(2023, 1, 5), result.WorkingDayRangeStart);
        Assert.Equal(new DateOnly(2026, 3, 31), result.WorkingDayRangeEnd);
    }

    [Fact]
    public void 設定が取り込まれる()
    {
        var result = Read();

        Assert.Equal(14, result.Settings.Count);
        Assert.Equal("light", result.Settings["theme"]);
        // 構造のある値は JSON のまま持つ
        Assert.StartsWith("[", result.Settings["categories"]);
    }

    // ------------------------------------------------------------------
    // 変換ログ（Phase 2 の完了条件）
    // ------------------------------------------------------------------

    [Fact]
    public void 変換ログが出る()
    {
        var result = Read();

        Assert.NotEmpty(result.Log);

        var text = result.FormatLog();
        Assert.Contains("旧データの取り込み結果", text);
        Assert.Contains("ToDo からの変換 13 件", text);
        Assert.Contains("稼働日      : 753 件", text);
    }

    [Fact]
    public void タスク変換は1件ずつログに残る()
    {
        var result = Read();

        var entries = result.Log.Where(e => e.Category == "タスク変換" && e.TargetId is not null).ToArray();

        // 13 件すべてについて、どれをどう変換したか追える
        Assert.Equal(13, entries.Length);
        Assert.All(entries, e => Assert.Contains("タスクへ変換", e.Message));
    }

    [Fact]
    public void 同期トークンを引き継げないことが警告される()
    {
        var result = Read();

        // 黙って全再同期になると、原因が分からないまま大量の通信が走る
        Assert.Contains(result.Log, e =>
            e.Level == ImportLogLevel.Warning && e.Category == "同期状態");
    }

    [Fact]
    public void データを失う変換は起きていない()
    {
        var result = Read();

        // エラーは「取り込めなかった」を意味する。実データで出てはいけない
        Assert.DoesNotContain(result.Log, e => e.Level == ImportLogLevel.Error);
    }

    // ------------------------------------------------------------------
    // データベースへの反映
    // ------------------------------------------------------------------

    [Fact]
    public void 読み込んでデータベースに書き込める()
    {
        using var db = TestDatabase.Create();
        using var stream = TestDatabase.OpenLegacyBackup();

        var result = new LegacyBackupMigrator(db.Connection).Migrate(stream);

        Assert.Equal(result.Events.Count, new EventRepository(db.Connection).Count());
        Assert.Equal(result.Tasks.Count, new TaskRepository(db.Connection).Count());
        Assert.Equal(753, new WorkingDayRepository(db.Connection).Count());
        Assert.Equal(14, new SettingsRepository(db.Connection).All().Count);
    }

    [Fact]
    public void 移行後は同期状態が空になる()
    {
        using var db = TestDatabase.Create();
        var settings = new SettingsRepository(db.Connection);

        // 古いトークンが残っていると差分同期が噛み合わず取りこぼす
        settings.SetSyncState("calendar.syncToken", "古いトークン");

        using var stream = TestDatabase.OpenLegacyBackup();
        new LegacyBackupMigrator(db.Connection).Migrate(stream);

        Assert.Null(settings.GetSyncState("calendar.syncToken"));
    }

    [Fact]
    public void 二度取り込んでも重複しない()
    {
        using var db = TestDatabase.Create();
        var migrator = new LegacyBackupMigrator(db.Connection);

        using (var first = TestDatabase.OpenLegacyBackup()) migrator.Migrate(first);
        var afterFirst = new EventRepository(db.Connection).Count();

        using (var second = TestDatabase.OpenLegacyBackup()) migrator.Migrate(second);

        // uid を主キーにしているので、同じファイルを二度読んでも増えない
        Assert.Equal(afterFirst, new EventRepository(db.Connection).Count());
    }

    [Fact]
    public void 移行してもマイルストーンは消えない()
    {
        using var db = TestDatabase.Create();
        var workingDays = new WorkingDayRepository(db.Connection);

        // 先に Excel からマイルストーンを取り込んである状態を作る
        workingDays.Save(Core.WorkingDays.WorkingDayCalendar.Create(
            [new DateOnly(2025, 1, 10)],
            new DateOnly(2025, 1, 10), new DateOnly(2025, 1, 10),
            [new Core.WorkingDays.Milestone(new DateOnly(2025, 1, 10), "仕様期限", "Ver．25.1")],
            new DateOnly(2025, 1, 10), new DateOnly(2025, 1, 10)));

        using var stream = TestDatabase.OpenLegacyBackup();
        new LegacyBackupMigrator(db.Connection).Migrate(stream);

        // バックアップにマイルストーンは含まれない。上書きで消してはいけない
        Assert.Equal(1, workingDays.MilestoneCount());
    }

    [Fact]
    public void 壊れたJSONは読み込みを止める()
    {
        using var stream = new MemoryStream("これは JSON ではない"u8.ToArray());

        var e = Assert.Throws<InvalidDataException>(() => LegacyBackupMigrator.Read(stream));
        Assert.Contains("JSON として読めませんでした", e.Message);
    }
}
