using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 題・場所・メモから探す。
/// <para>並びは今日に近い順。件数を絞ったときに古いものだけが残らないようにする。</para>
/// </summary>
public class SearchTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static MainViewModel Create(TestWorkspace test) => new(test.Workspace, Today);

    private static void Add(TestWorkspace test, string id, string title, DateOnly date,
        string? location = null, string? note = null) =>
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = id, Title = title, Date = date, Location = location, Note = note,
        });

    [Fact]
    public void 題で見つかる()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "課内会議", Today);
        Add(test, "e2", "出張", Today);

        var main = Create(test);
        main.SearchText = "会議";

        Assert.Equal("e1", main.SearchResults.Single().Id);
    }

    [Fact]
    public void 場所とメモでも見つかる()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "打ち合わせ", Today, location: "第2会議室");
        Add(test, "e2", "検討", Today, note: "資料を第2会議室に置く");

        var main = Create(test);
        main.SearchText = "第2会議室";

        Assert.Equal(2, main.SearchResults.Count);
    }

    [Fact]
    public void 大文字小文字は区別しない()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "VENUS 打ち合わせ", Today);

        var main = Create(test);
        main.SearchText = "venus";

        Assert.Single(main.SearchResults);
    }

    [Fact]
    public void タスクも探す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = Today });

        var main = Create(test);
        main.SearchText = "資料";

        Assert.True(main.SearchResults.Single().IsTask);
    }

    [Fact]
    public void 期限の無いタスクも出す()
    {
        // 保存はされるのに検索からも見えなくなっていた分（項目1）。並びには選んでいる日を
        // 代用するが、画面には「期限なし」と出す（HasDue で判定）
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成" });

        var main = Create(test);
        main.SearchText = "資料";

        var found = main.SearchResults.Single();
        Assert.True(found.IsTask);
        Assert.False(found.HasDue);
        Assert.Equal("期限なし", found.DateText);
        Assert.Equal(main.SelectedDate, found.Date);
    }

    [Fact]
    public void 実働日データの印は出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:2026-09-24", Title = CalendarWorkspace.ClosedDayTitle,
            Date = Today, Source = CalendarWorkspace.WorkingDaySource,
        });

        var main = Create(test);
        main.SearchText = "休業";

        Assert.Empty(main.SearchResults);
    }

    [Fact]
    public void 今日に近いものから残す()
    {
        using var test = TestWorkspace.Create();
        for (var i = 1; i <= 60; i++) Add(test, $"old{i}", "定例", Today.AddDays(-i));

        Add(test, "near", "定例", Today.AddDays(1));

        var main = Create(test);
        main.SearchText = "定例";

        // 50件に絞っても、近いものが落ちてはいけない
        Assert.Equal(50, main.SearchResults.Count);
        Assert.Contains(main.SearchResults, r => r.Id == "near");
    }

    [Fact]
    public void 出すときは日付の昇順()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "定例", Today.AddDays(3));
        Add(test, "e2", "定例", Today.AddDays(-1));
        Add(test, "e3", "定例", Today.AddDays(1));

        var main = Create(test);
        main.SearchText = "定例";

        Assert.Equal(["e2", "e3", "e1"], main.SearchResults.Select(r => r.Id));
    }

    [Fact]
    public void 空にすると結果も消える()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "課内会議", Today);

        var main = Create(test);
        main.SearchText = "会議";
        Assert.NotEmpty(main.SearchResults);

        main.ClearSearch();

        Assert.Empty(main.SearchResults);
        Assert.False(main.IsSearching);
        Assert.Null(main.SearchMessage);
    }

    [Fact]
    public void 選ぶとその日へ移る()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "課内会議", Today.AddDays(5));

        var main = Create(test);
        main.SearchText = "会議";
        main.OpenSearchResult(main.SearchResults.Single());

        Assert.Equal(Today.AddDays(5), main.SelectedDate);
        Assert.False(main.IsSearching);
    }
}
