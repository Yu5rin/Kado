using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 通知するかどうかを、予定ごととカレンダーごとに決める。
/// <para>予定ごとの指定が勝ち、無ければカレンダーの決まりに従う。</para>
/// </summary>
public class NotifyToggleTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static CalendarEvent Event(string id, string calendarId, bool? notify = null) => new()
    {
        Id = id, Title = "課内会議", Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        CalendarId = calendarId, Notify = notify,
    };

    [Fact]
    public void 既定では知らせる()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");

        Assert.True(test.Workspace.NotifiesFor(Event("e1", calendar.Id)));
    }

    [Fact]
    public void カレンダーのベルを切ると知らせない()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");

        test.Workspace.SetCalendarNotify(calendar.Id, false);

        Assert.False(test.Workspace.NotifiesFor(Event("e1", calendar.Id)));
    }

    [Fact]
    public void 予定ごとの指定が勝つ()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");
        test.Workspace.SetCalendarNotify(calendar.Id, false);

        // カレンダーは切ってあるが、この予定だけは知らせたい
        Assert.True(test.Workspace.NotifiesFor(Event("e1", calendar.Id, notify: true)));

        test.Workspace.SetCalendarNotify(calendar.Id, true);
        Assert.False(test.Workspace.NotifiesFor(Event("e2", calendar.Id, notify: false)));
    }

    [Fact]
    public void ベルの状態は保存される()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");

        test.Workspace.SetCalendarNotify(calendar.Id, false);

        Assert.False(test.Workspace.Sources.Calendars().Single(c => c.Id == calendar.Id).NotifyDefault);
    }

    [Fact]
    public void 知らせない予定は通知に出ない()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");
        test.Workspace.SetCalendarNotify(calendar.Id, false);
        test.Workspace.AddEvent(Event("e1", calendar.Id));

        var settings = new AppSettings(test.Workspace.Settings) { NotifyEnabled = true, NotifyLeadMinutes = 10 };
        var notifier = new FakeNotifier();
        var service = new Notifications.ReminderService(test.Workspace, settings, notifier);

        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public void 左パネルのベルで切り替わる()
    {
        using var test = TestWorkspace.Create();
        var calendar = test.Workspace.CreateCalendar("仕事");

        var lists = new SourceListsViewModel(test.Workspace);
        var row = lists.Calendars.Single(c => c.Id == calendar.Id);

        Assert.True(row.Notifies);
        Assert.True(row.HasNotifyToggle);

        row.Notifies = false;

        Assert.False(test.Workspace.Sources.Calendars().Single(c => c.Id == calendar.Id).NotifyDefault);
    }

    [Fact]
    public void タスクリストにはベルを出さない()
    {
        using var test = TestWorkspace.Create();
        var lists = new SourceListsViewModel(test.Workspace);

        Assert.All(lists.TaskLists, t => Assert.False(t.HasNotifyToggle));
    }
}
