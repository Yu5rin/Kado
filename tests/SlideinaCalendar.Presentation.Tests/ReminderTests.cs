using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Notifications;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>知らせた中身を控えるだけの実装。</summary>
internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message)> Sent { get; } = [];

    public bool IsSupported => true;

    public void Notify(string title, string message) => Sent.Add((title, message));
}

/// <summary>
/// 予定の前と、朝のまとめを知らせる。
/// <para>1分ごとに「いま知らせるものがあるか」を見る形なので、時計を進めて確かめる。</para>
/// </summary>
public class ReminderTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static void AddEvent(TestWorkspace test, string id, TimeOnly start, string title = "課内会議") =>
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = id, Title = title, Date = Today, StartTime = start, EndTime = start.AddHours(1),
        });

    private static (FakeNotifier Notifier, ReminderService Service) Create(
        TestWorkspace test, Action<AppSettings> configure)
    {
        var settings = new AppSettings(test.Workspace.Settings);
        configure(settings);

        var notifier = new FakeNotifier();
        return (notifier, new ReminderService(test.Workspace, settings, notifier));
    }

    [Fact]
    public void 予定の前に知らせる()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
        });

        service.Check(new DateTime(2026, 9, 24, 9, 45, 0));
        Assert.Empty(notifier.Sent);

        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.Equal("課内会議", notifier.Sent.Single().Title);
        Assert.Contains("10:00", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 同じ予定を何度も知らせない()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
        });

        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));
        service.Check(new DateTime(2026, 9, 24, 9, 52, 0));
        service.Check(new DateTime(2026, 9, 24, 9, 53, 0));

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public void 始まってしまった予定は知らせない()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
        });

        // アプリを開いたのが予定のあとだった、という場合
        service.Check(new DateTime(2026, 9, 24, 11, 0, 0));

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public void 切っていれば知らせない()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s => s.NotifyEnabled = false);
        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public void 朝にその日の予定をまとめて知らせる()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));
        AddEvent(test, "e2", new TimeOnly(14, 0), "検討会");

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 7, 59, 0));
        Assert.Empty(notifier.Sent);

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();
        Assert.Contains("2件", sent.Title);
        Assert.Contains("課内会議", sent.Message);
        Assert.Contains("検討会", sent.Message);
    }

    [Fact]
    public void まとめは1日に1回だけ()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));
        service.Check(new DateTime(2026, 9, 24, 9, 0, 0));

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public void 予定が無い日もまとめは出す()
    {
        using var test = TestWorkspace.Create();

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        Assert.Contains("予定はありません", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 実働日データの印は知らせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:a", Title = CalendarWorkspace.ClosedDayTitle, Date = Today,
            Source = CalendarWorkspace.WorkingDaySource,
        });

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        Assert.Contains("予定はありません", notifier.Sent.Single().Message);
    }
}
