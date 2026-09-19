using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Data.Tests;

public class EventRepositoryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Sample(string id, DateOnly date, TimeOnly? start = null) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = date,
        StartTime = start,
        EndTime = start?.AddHours(1),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void 書いて読める()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Sample("e1", D(2026, 9, 24), new TimeOnly(9, 30)));
        var found = repo.Find("e1");

        Assert.NotNull(found);
        Assert.Equal("予定e1", found.Title);
        Assert.Equal(D(2026, 9, 24), found.Date);
        Assert.Equal(new TimeOnly(9, 30), found.StartTime);
        Assert.Equal(new TimeOnly(10, 30), found.EndTime);
        Assert.False(found.IsAllDay);
    }

    [Fact]
    public void 終日予定は時刻を持たない()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Sample("e1", D(2026, 9, 24)));

        var found = repo.Find("e1")!;
        Assert.True(found.IsAllDay);
        Assert.Null(found.StartTime);
    }

    [Fact]
    public void 同じIDで上書きされる()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Sample("e1", D(2026, 9, 24)));
        repo.Upsert(Sample("e1", D(2026, 9, 24)) with { Title = "書き換え後" });

        Assert.Equal(1, repo.Count());
        Assert.Equal("書き換え後", repo.Find("e1")!.Title);
    }

    [Fact]
    public void 期間で引ける()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.UpsertMany([
            Sample("before", D(2026, 9, 20)),
            Sample("inside", D(2026, 9, 24)),
            Sample("after", D(2026, 9, 30)),
        ]);

        var found = repo.InRange(D(2026, 9, 22), D(2026, 9, 26));
        Assert.Equal("inside", Assert.Single(found).Id);
    }

    [Fact]
    public void 複数日予定は終了日まで期間に含まれる()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        // 9/20 開始で 9/25 まで続く予定は、9/24 を含む期間に入る
        repo.Upsert(Sample("multi", D(2026, 9, 20)) with { EndDate = D(2026, 9, 25) });

        Assert.Single(repo.InRange(D(2026, 9, 24), D(2026, 9, 24)));
        Assert.Empty(repo.InRange(D(2026, 9, 26), D(2026, 9, 30)));
    }

    [Fact]
    public void 繰り返し予定は期間検索に出ず別に取れる()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        // 開始日しか持たないため日付では絞れない。呼び出し側が展開する
        repo.Upsert(Sample("r1", D(2020, 1, 1)) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        repo.Upsert(Sample("single", D(2026, 9, 24)));

        Assert.Equal("single", Assert.Single(repo.InRange(D(2026, 9, 1), D(2026, 9, 30))).Id);
        Assert.Equal("r1", Assert.Single(repo.AllRecurring()).Id);
    }

    [Fact]
    public void GoogleのIDで引ける()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Sample("e1", D(2026, 9, 24)) with { GoogleEventId = "gcal-1" });

        Assert.Equal("e1", repo.FindByGoogleId("gcal-1")!.Id);
        Assert.Null(repo.FindByGoogleId("いない"));
    }

    [Fact]
    public void 削除できる()
    {
        using var db = TestDatabase.Create();
        var repo = new EventRepository(db.Connection);

        repo.Upsert(Sample("e1", D(2026, 9, 24)));

        Assert.True(repo.Delete("e1"));
        Assert.False(repo.Delete("e1"));
        Assert.Equal(0, repo.Count());
    }
}

public class TaskRepositoryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void 期限なしのタスクを持てる()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(new TaskItem { Id = "t1", Title = "いつかやる" });

        var found = repo.Find("t1")!;
        Assert.Null(found.Due);
        Assert.False(found.HasDue);
        Assert.Single(repo.WithoutDue());
    }

    [Fact]
    public void 期限で引ける()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.UpsertMany([
            new TaskItem { Id = "t1", Title = "期限あり", Due = D(2026, 9, 24) },
            new TaskItem { Id = "t2", Title = "期限なし" },
            new TaskItem { Id = "t3", Title = "範囲外", Due = D(2026, 12, 1) },
        ]);

        Assert.Equal("t1", Assert.Single(repo.DueInRange(D(2026, 9, 1), D(2026, 9, 30))).Id);
    }

    [Fact]
    public void 完了状態を数えられる()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.UpsertMany([
            new TaskItem { Id = "t1", Title = "未完了" },
            new TaskItem { Id = "t2", Title = "完了", IsDone = true },
        ]);

        Assert.Equal(2, repo.Count());
        Assert.Equal(1, repo.CountOpen());
    }

    [Fact]
    public void 作業時間ブロックを持てる()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(new TaskItem { Id = "t1", Title = "作業のあるタスク" });
        repo.UpsertBlock(new WorkBlock { Id = "b1", TaskId = "t1", Date = D(2026, 9, 24), StartTime = new TimeOnly(9, 0), DurationMinutes = 90 });

        var block = Assert.Single(repo.BlocksOf("t1"));
        Assert.Equal(new TimeOnly(10, 30), block.EndTime);
        Assert.Single(repo.BlocksInRange(D(2026, 9, 24), D(2026, 9, 24)));
    }

    [Fact]
    public void タスクを消すと作業時間ブロックも消える()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(new TaskItem { Id = "t1", Title = "作業のあるタスク" });
        repo.UpsertBlock(new WorkBlock { Id = "b1", TaskId = "t1", Date = D(2026, 9, 24), StartTime = new TimeOnly(9, 0), DurationMinutes = 60 });

        repo.Delete("t1");

        Assert.Empty(repo.BlocksOf("t1"));
    }
}

public class WorkingDayRepositoryTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void カレンダーを往復できる()
    {
        using var db = TestDatabase.Create();
        var repo = new WorkingDayRepository(db.Connection);

        var original = WorkingDayCalendar.Create(
            [D(2026, 9, 1), D(2026, 9, 2)],
            D(2026, 9, 1), D(2026, 9, 30),
            [new Milestone(D(2026, 9, 2), "仕様期限", "Ver．26.1")],
            D(2026, 9, 2), D(2026, 9, 2));

        repo.Save(original);
        var loaded = repo.Load();

        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.IsWorkingDay(D(2026, 9, 1)));
        Assert.Equal(D(2026, 9, 1), loaded.RangeStart);
        Assert.Equal(D(2026, 9, 30), loaded.RangeEnd);

        var milestone = Assert.Single(loaded.MilestonesOn(D(2026, 9, 2)));
        Assert.Equal("仕様期限", milestone.Name);
        Assert.Equal("Ver．26.1", milestone.SourceVersion);
        Assert.Equal(D(2026, 9, 2), loaded.MilestoneRangeStart);
    }

    [Fact]
    public void 空のカレンダーを読んでも壊れない()
    {
        using var db = TestDatabase.Create();
        var loaded = new WorkingDayRepository(db.Connection).Load();

        Assert.Equal(0, loaded.Count);
        Assert.Null(loaded.RangeStart);
    }
}

public class SettingsRepositoryTests
{
    [Fact]
    public void 設定を書いて読める()
    {
        using var db = TestDatabase.Create();
        var repo = new SettingsRepository(db.Connection);

        repo.Set("theme", "dark");

        Assert.Equal("dark", repo.Get("theme"));
        Assert.Equal("light", repo.GetOrDefault("いないキー", "light"));
        Assert.Single(repo.All());
    }

    [Fact]
    public void 同期状態は設定とは別に持つ()
    {
        using var db = TestDatabase.Create();
        var repo = new SettingsRepository(db.Connection);

        repo.Set("theme", "dark");
        repo.SetSyncState("calendar.syncToken", "token-1");

        repo.ClearSyncState();

        // 全再同期のために同期状態だけを捨てても、設定は残る
        Assert.Null(repo.GetSyncState("calendar.syncToken"));
        Assert.Equal("dark", repo.Get("theme"));
    }
}
