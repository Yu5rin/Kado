using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;

namespace SlideinaCalendar.Data.Tests;

/// <summary>
/// カレンダーとタスクリストの表。
/// <para>複数カレンダーの取得・表示 ON/OFF・並び順は落とせない処理（要件書 6.3）。</para>
/// </summary>
public class SourceRepositoryTests
{
    private static CalendarSource Calendar(string id, string summary, int order = 0) => new()
    {
        Id = id, Summary = summary, SortOrder = order, UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void 登録して並び順で読める()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("b", "仕事", order: 1));
        repository.Upsert(Calendar("a", "私用", order: 0));

        Assert.Equal(["私用", "仕事"], repository.Calendars().Select(c => c.Summary));
    }

    [Fact]
    public void 取り込み直してもチェックは戻らない()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("a", "仕事"));
        repository.SetCalendarVisible("a", false);

        // 同期のたびにチェックが戻ると使い物にならない
        repository.Upsert(Calendar("a", "仕事（名前が変わった）"));

        var stored = Assert.Single(repository.Calendars());
        Assert.False(stored.IsVisible);
        Assert.Equal("仕事（名前が変わった）", stored.Summary);
    }

    [Fact]
    public void 並び順を入れ替えられる()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("a", "A"));
        repository.Upsert(Calendar("b", "B"));
        repository.Upsert(Calendar("c", "C"));

        repository.SetCalendarOrder(["c", "a", "b"]);

        Assert.Equal(["C", "A", "B"], repository.Calendars().Select(x => x.Summary));
    }

    [Fact]
    public void 消えたカレンダーを落とせる()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("a", "残る"));
        repository.Upsert(Calendar("b", "消える"));

        Assert.Equal(1, repository.RemoveCalendarsExcept(["a"]));
        Assert.Equal("残る", Assert.Single(repository.Calendars()).Summary);
    }

    [Fact]
    public void 全部消すこともできる()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("a", "仕事"));

        Assert.Equal(1, repository.RemoveCalendarsExcept([]));
        Assert.Empty(repository.Calendars());
    }

    [Fact]
    public void 付け替えた表示名が優先される()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(Calendar("a", "本名") with { SummaryOverride = "呼び名" });

        Assert.Equal("呼び名", Assert.Single(repository.Calendars()).DisplayName);
    }

    [Fact]
    public void タスクリストも同じように扱える()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(new TaskListSource
        {
            Id = "t1", Title = "マイタスク", UpdatedAt = DateTimeOffset.Now,
        });

        repository.SetTaskListVisible("t1", false);

        var stored = Assert.Single(repository.TaskLists());
        Assert.Equal("マイタスク", stored.DisplayName);
        Assert.False(stored.IsVisible);
    }
}
