using Kado.Data.Models;
using Kado.Presentation.Notifications;
using Kado.Presentation.Settings;

namespace Kado.Presentation.Tests;

/// <summary>知らせた中身を控えるだけの実装。</summary>
internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message)> Sent { get; } = [];

    public bool IsSupported => true;

    /// <summary>音を鳴らすよう頼まれたか。</summary>
    public bool LastWithSound { get; private set; }

    public void Notify(string title, string message, bool withSound = true)
    {
        Sent.Add((title, message));
        LastWithSound = withSound;
    }
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

    // ------------------------------------------------------------------
    // 知らせた記録の片付け（朝のまとめとは切り離す）
    // ------------------------------------------------------------------

    [Fact]
    public void 日が変わってから朝のまとめまでの間も予定は1回だけ知らせる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "早朝の打ち合わせ", Date = Today.AddDays(1),
            StartTime = new TimeOnly(0, 40), EndTime = new TimeOnly(1, 0),
        });

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        // 前の日の朝のまとめは済んでいる
        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));
        notifier.Sent.Clear();

        // 日が変わった。まとめの時刻（8:00）までは、前の日の印のまま
        service.Check(new DateTime(2026, 9, 25, 0, 31, 0));
        service.Check(new DateTime(2026, 9, 25, 0, 32, 0));
        service.Check(new DateTime(2026, 9, 25, 0, 33, 0));

        Assert.Equal("早朝の打ち合わせ", notifier.Sent.Single().Title);
    }

    [Fact]
    public void 日をまたぐ前に知らせた予定を0時過ぎに再び出さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "夜更けの当番", Date = Today.AddDays(1),
            StartTime = new TimeOnly(0, 5), EndTime = new TimeOnly(0, 30),
        });

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));
        notifier.Sent.Clear();

        // 23:55 に知らせた（まだ前の日）
        service.Check(new DateTime(2026, 9, 24, 23, 55, 0));
        Assert.Single(notifier.Sent);

        // 日が変わっても、同じ予定をもう一度は出さない
        service.Check(new DateTime(2026, 9, 25, 0, 0, 0));
        service.Check(new DateTime(2026, 9, 25, 0, 1, 0));

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public void 朝のまとめを切っていても知らせた記録は溜まり続けない()
    {
        using var test = TestWorkspace.Create();
        var (_, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
            s.SummaryEnabled = false;
        });

        // 10日分、毎日1件ずつ知らせる
        for (var day = 0; day < 10; day++)
        {
            var date = Today.AddDays(day);
            test.Workspace.AddEvent(new CalendarEvent
            {
                Id = $"e{day}", Title = "定例", Date = date,
                StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
            });

            service.Check(date.ToDateTime(new TimeOnly(9, 51)));
        }

        // 昨日より前の記録は捨てる。残るのは昨日と今日のぶんだけ
        Assert.True(service.NotifiedCount <= 2, $"記録が {service.NotifiedCount} 件残っている");
    }

    [Fact]
    public void 予定ごとの通知の確認でカレンダー一覧を何度も読まない()
    {
        using var test = TestWorkspace.Create();
        for (var i = 0; i < 5; i++) AddEvent(test, $"e{i}", new TimeOnly(10, 0), $"会議{i}");

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
        });

        using var counter = SqlCounter.Attach(test.Connection);
        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.Equal(5, notifier.Sent.Count);
        Assert.Equal(1, counter.CalendarReads);
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
    public void まとめに同じ予定を二度出さない()
    {
        using var test = TestWorkspace.Create();

        // 同じ予定を2つのカレンダーに持っていると、そのぶん二度出ていた
        var other = test.Workspace.CreateCalendar("共有");

        AddEvent(test, "e1", new TimeOnly(23, 0), "MAD5");
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "MAD5", Date = Today,
            StartTime = new TimeOnly(23, 0), EndTime = new TimeOnly(23, 55),
            CalendarId = other.Id,
        });

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();
        Assert.Contains("1件", sent.Title);
        Assert.Equal("23:00 MAD5", sent.Message);
    }

    [Fact]
    public void 出していないカレンダーはまとめに入れない()
    {
        using var test = TestWorkspace.Create();

        var hidden = test.Workspace.CreateCalendar("下書き");
        test.Workspace.Sources.SetCalendarVisible(hidden.Id, false);

        AddEvent(test, "e1", new TimeOnly(10, 0));
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e2", Title = "見せない予定", Date = Today,
            StartTime = new TimeOnly(14, 0), CalendarId = hidden.Id,
        });

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();
        Assert.Contains("1件", sent.Title);
        Assert.DoesNotContain("見せない予定", sent.Message);
    }

    [Fact]
    public void 知らせない設定のカレンダーもまとめには入れる()
    {
        using var test = TestWorkspace.Create();

        var quiet = test.Workspace.CreateCalendar("誕生日");
        test.Workspace.SetCalendarNotify(quiet.Id, false);

        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "誰かの誕生日", Date = Today, CalendarId = quiet.Id,
        });

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        // 「知らせない」は予定ごとの通知を止める指定。朝のまとめは
        // その日に何があるかを並べるもので、役割が違う
        var sent = notifier.Sent.Single();
        Assert.Contains("誰かの誕生日", sent.Message);
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

    // ------------------------------------------------------------------
    // タスクの期限通知（項目3）。朝のまとめに「今日まで／遅れ」の行を足す
    // ------------------------------------------------------------------

    private static void AddTask(
        TestWorkspace test, string id, DateOnly? due, string title = "部品表確認",
        bool isDone = false, string? taskListId = null) =>
        test.Workspace.AddTask(new TaskItem
        {
            Id = id, Title = title, Due = due, IsDone = isDone, TaskListId = taskListId,
        });

    [Fact]
    public void 今日までのタスクをまとめに足す()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", Today);

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();
        Assert.Contains("今日まで 部品表確認", sent.Message);
    }

    [Fact]
    public void 遅れているタスクをまとめに足す()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", Today.AddDays(-3), "見積提出");

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();
        Assert.Contains("遅れ", sent.Message);
        Assert.Contains("見積提出", sent.Message);
    }

    [Fact]
    public void まだ先のタスクはまとめに足さない()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", Today.AddDays(10), "来月の準備");

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        // 先の予告まで出すと長くなりすぎる。今日と遅れだけに絞る
        Assert.DoesNotContain("来月の準備", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 完了済みタスクはまとめに足さない()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", Today, "片付いた作業", isDone: true);

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        Assert.DoesNotContain("片付いた作業", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 期限なしタスクはまとめに足さない()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", due: null, title: "いつかやる");

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        Assert.DoesNotContain("いつかやる", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 表示を切ったタスクリストのものはまとめに足さない()
    {
        using var test = TestWorkspace.Create();

        var hidden = test.Workspace.CreateTaskList("下書き");
        test.Workspace.Sources.SetTaskListVisible(hidden.Id, false);

        AddTask(test, "t1", Today, "見せないタスク", taskListId: hidden.Id);

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        Assert.DoesNotContain("見せないタスク", notifier.Sent.Single().Message);
    }

    [Fact]
    public void 予定が無くてもタスクだけでまとめを出す()
    {
        using var test = TestWorkspace.Create();
        AddTask(test, "t1", Today, "唯一のタスク");

        var (notifier, service) = Create(test, s =>
        {
            s.SummaryEnabled = true;
            s.SummaryTime = new TimeOnly(8, 0);
        });

        service.Check(new DateTime(2026, 9, 24, 8, 0, 0));

        var sent = notifier.Sent.Single();

        // 予定側は0件のままなのが正しい。以前ここを取り違えて、表示している
        // カレンダーで絞るべきところを通知の設定でも絞ってしまい、タスクがあるのに
        // 「本日の予定はありません」と出た不具合があった。同じ轍を踏まないよう、
        // 予定が無いことと通知そのものが出ないことを分けて確かめる
        Assert.Contains("0件", sent.Title);
        Assert.Contains("唯一のタスク", sent.Message);
    }

    [Fact]
    public void 音を鳴らすかは設定に従う()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
            s.NotifySound = false;
        });

        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.Single(notifier.Sent);
        Assert.False(notifier.LastWithSound);
    }

    [Fact]
    public void 既定では音を鳴らす()
    {
        using var test = TestWorkspace.Create();
        AddEvent(test, "e1", new TimeOnly(10, 0));

        var (notifier, service) = Create(test, s =>
        {
            s.NotifyEnabled = true;
            s.NotifyLeadMinutes = 10;
        });

        service.Check(new DateTime(2026, 9, 24, 9, 51, 0));

        Assert.True(notifier.LastWithSound);
    }
}
