using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 同じ内容の予定を1つにまとめる。
/// <para>取り込みや再連携で、同じ予定が2つできることがある。</para>
/// </summary>
public class DuplicateTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static CalendarEvent Event(string id, string title = "課内会議", string? location = null,
        string? googleId = null) => new()
    {
        Id = id, Title = title, Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        CalendarId = "local:default", Location = location, GoogleEventId = googleId,
    };

    [Fact]
    public void 同じ内容が2件あれば1件を挙げる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2"));

        Assert.Single(test.Workspace.FindDuplicateEvents());
    }

    [Fact]
    public void 中身の多いほうを残す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2", location: "第2会議室", googleId: "g1"));

        Assert.Equal("e1", test.Workspace.FindDuplicateEvents().Single().Id);
    }

    [Fact]
    public void 時刻が違えば別物()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2") with { StartTime = new TimeOnly(14, 0) });

        Assert.Empty(test.Workspace.FindDuplicateEvents());
    }

    [Fact]
    public void カレンダーが違えば別物()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2") with { CalendarId = "local:other" });

        Assert.Empty(test.Workspace.FindDuplicateEvents());
    }

    [Fact]
    public void 実働日データの印は対象にしない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:a", Title = CalendarWorkspace.ClosedDayTitle, Date = Today,
            Source = CalendarWorkspace.WorkingDaySource,
        });
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:b", Title = CalendarWorkspace.ClosedDayTitle, Date = Today,
            Source = CalendarWorkspace.WorkingDaySource,
        });

        Assert.Empty(test.Workspace.FindDuplicateEvents());
    }

    [Fact]
    public void 消したらまとめて戻せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2"));
        test.Workspace.AddEvent(Event("e3", title: "別の会議"));
        test.Workspace.AddEvent(Event("e4", title: "別の会議"));

        Assert.Equal(2, test.Workspace.RemoveDuplicateEvents());
        Assert.Equal(2, test.Workspace.Events.All().Count);

        // Ctrl＋Z ひと押しで戻る
        test.Workspace.UndoLast();

        Assert.Equal(4, test.Workspace.Events.All().Count);
    }

    [Fact]
    public void 尋ねてから消す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));
        test.Workspace.AddEvent(Event("e2"));

        var files = new FakeFileDialogs { Confirms = false };
        var main = new MainViewModel(test.Workspace, Today, files: files);

        main.RemoveDuplicatesCommand.Execute(null);

        Assert.Equal(2, test.Workspace.Events.All().Count);
    }

    [Fact]
    public void 重複が無ければそう伝える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1"));

        var main = new MainViewModel(test.Workspace, Today, files: new FakeFileDialogs());
        main.RemoveDuplicatesCommand.Execute(null);

        Assert.Contains("見つかりませんでした", main.StatusMessage);
    }
}
