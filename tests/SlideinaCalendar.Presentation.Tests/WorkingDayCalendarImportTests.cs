using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 実働日を取り込むと「inaCalendar」へ入る。
/// <para>
/// 旧 inaCalendar と同じ名前にしてある。向こうは Google 側にこの名前のカレンダーを
/// 作って書き込んでいた。同じ名前にしておけば、繋いだときに同じところへ集まる。
/// </para>
/// </summary>
public class WorkingDayCalendarImportTests
{
    private static Stream SampleFile()
    {
        var path = Find("実働日サンプル.xlsx");
        return File.OpenRead(path);
    }

    private static string Find(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var hit = directory.GetFiles(name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit.FullName;

            directory = directory.Parent;
        }

        throw new FileNotFoundException(name);
    }

    [Fact]
    public void 取り込むとinaCalendarができる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        Assert.Contains(
            test.Workspace.Sources.Calendars(),
            c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);
    }

    [Fact]
    public void マイルストーンが予定として入る()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using var file = SampleFile();
        var result = test.Workspace.ImportWorkingDays(file);

        var calendar = test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        // 休業日・特別出勤の印も同じカレンダーに入るので、マイルストーンだけを数える
        var written = test.Workspace.Events.All()
            .Where(e => e.CalendarId == calendar.Id && CalendarWorkspace.IsMilestoneId(e.Id))
            .ToArray();

        Assert.Equal(result.Milestones.Count, written.Length);
        Assert.All(written, e => Assert.Equal(CalendarWorkspace.WorkingDaySource, e.Source));
        Assert.Contains(written, e => e.Title == "仕様期限");
    }

    [Fact]
    public void 予定の並びには出さない()
    {
        // 月ビューでは日付の行に別途出している。外さないと同じ日に二度出る
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        var sources = new SourceListsViewModel(test.Workspace);

        var milestone = test.Workspace.Events.All()
            .First(e => e.Source == CalendarWorkspace.WorkingDaySource);

        Assert.False(sources.IncludesEvent(milestone));
    }

    [Fact]
    public void 読み直しても増えない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using var first = SampleFile();
        test.Workspace.ImportWorkingDays(first);
        var afterFirst = test.Workspace.Events.Count();

        using var second = SampleFile();
        test.Workspace.ImportWorkingDays(second);

        // 同じ日の同じ名前なら同じ予定として扱う
        Assert.Equal(afterFirst, test.Workspace.Events.Count());
    }

    [Fact]
    public void 手で入れた予定は消さない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        test.Workspace.AddEvent(new Data.Models.CalendarEvent
        {
            Id = "e1", Title = "自分の予定", Date = new DateOnly(2025, 1, 10),
        });

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        // 取り込みで入れ替えるのは、実働日データから起こしたものだけ
        Assert.NotNull(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void すでに同じ名前のカレンダーがあればそれを使う()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        // Google から取り込んだ「inaCalendar」がある、という想定
        var existing = test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        // 同じ名前のものを二つ作らない
        Assert.Single(
            test.Workspace.Sources.Calendars(),
            c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        Assert.Contains(test.Workspace.Events.All(), e => e.CalendarId == existing.Id);
    }

    // ------------------------------------------------------------------
    // 入れ先の選び方
    //
    // Google に繋いでいれば Google の「inaCalendar」へ、繋いでいなければ
    // このアプリの「inaCalendar」へ。無ければ作る
    // ------------------------------------------------------------------

    private static void AddGoogleCalendar(TestWorkspace test, string id, string name) =>
        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = id,
            Summary = name,
            GoogleRaw = $$"""{"id":"{{id}}","summary":"{{name}}","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

    private static IReadOnlyList<CalendarEvent> Milestones(TestWorkspace test) =>
        test.Workspace.Events.All().Where(e => CalendarWorkspace.IsMilestoneId(e.Id)).ToArray();

    [Fact]
    public void 繋いでいなければこのアプリの_inaCalendar_に入れる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        var calendar = test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        Assert.True(CalendarWorkspace.IsLocal(calendar));
        Assert.All(Milestones(test), e => Assert.Equal(calendar.Id, e.CalendarId));
    }

    [Fact]
    public void _Google_に_inaCalendar_があればそちらに入れる()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        const string id = "ina@group.calendar.google.com";
        AddGoogleCalendar(test, id, CalendarWorkspace.WorkingDayCalendarName);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        Assert.All(Milestones(test), e => Assert.Equal(id, e.CalendarId));
    }

    [Fact]
    public void 両方あれば_Google_のほうを選ぶ()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);

        const string id = "ina@group.calendar.google.com";
        AddGoogleCalendar(test, id, CalendarWorkspace.WorkingDayCalendarName);

        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        Assert.All(Milestones(test), e => Assert.Equal(id, e.CalendarId));
    }

    [Fact]
    public void 同期を通したあとに読み直しても増えない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using (var first = SampleFile()) test.Workspace.ImportWorkingDays(first);

        var before = Milestones(test);
        Assert.NotEmpty(before);

        // 同期を通ると Source は "google" に書き換わり、Google の識別子が付く。
        // 入れ替えの見分けを Source でやっていると、ここで二重になる
        foreach (var e in before)
        {
            test.Workspace.Events.Upsert(e with
            {
                Source = "google",
                GoogleEventId = $"g-{e.Id}",
                UpdatedAt = DateTimeOffset.Now,
            });
        }

        using (var second = SampleFile()) test.Workspace.ImportWorkingDays(second);

        Assert.Equal(before.Count, Milestones(test).Count);

        // 結び付けた Google の識別子は残す。消して作り直すと相手側でも作り直しになる
        Assert.All(Milestones(test), e => Assert.NotNull(e.GoogleEventId));
    }

    [Fact]
    public void 利用者が自分で入れた予定は消さない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);

        using (var first = SampleFile()) test.Workspace.ImportWorkingDays(first);

        var calendar = test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        var mine = Milestones(test)[0].Date;
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "mine", Title = "チャンスの時間", Date = mine, CalendarId = calendar.Id,
            StartTime = new TimeOnly(23, 0), EndTime = new TimeOnly(23, 55),
        });

        using (var second = SampleFile()) test.Workspace.ImportWorkingDays(second);

        Assert.NotNull(test.Workspace.Events.Find("mine"));
    }

    // ------------------------------------------------------------------
    // 休業日も inaCalendar に入れる
    //
    // 拡張機能の inaCalendar と同じ。実働日データが持っているのは稼働日だけなので、
    // 期間内で稼働日でない日が休業日になる。
    //
    // 合成のサンプル Excel は平日をすべて稼働日にしてあるので、休業日を持つデータは
    // TestWorkspace の 2026年9月（敬老の日・国民の休日・秋分の日を除く）を使う
    // ------------------------------------------------------------------

    private static readonly DateOnly[] Closed =
    [
        new(2026, 9, 21), new(2026, 9, 22), new(2026, 9, 23),
    ];

    private static IReadOnlyList<CalendarEvent> ClosedDays(TestWorkspace test) =>
        test.Workspace.Events.All().Where(e => CalendarWorkspace.IsClosedDayId(e.Id)).ToArray();

    [Fact]
    public void 休業日も_inaCalendar_に入れる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        var calendar = test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == CalendarWorkspace.WorkingDayCalendarName);

        var closed = ClosedDays(test);

        Assert.Equal(Closed, closed.Select(e => e.Date).OrderBy(d => d));
        Assert.All(closed, e => Assert.Equal(calendar.Id, e.CalendarId));
        Assert.All(closed, e => Assert.Equal(CalendarWorkspace.ClosedDayTitle, e.Title));
    }

    [Fact]
    public void 土日は休業日に入れない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        // 月ビューでは背景が沈むうえ、毎週のことなので予定にすると連休が埋もれる
        Assert.All(ClosedDays(test), e =>
            Assert.False(e.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
    }

    [Fact]
    public void 稼働日は休業日にしない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        Assert.All(ClosedDays(test), e =>
            Assert.False(test.Workspace.WorkingDays.IsWorkingDay(e.Date)));
    }

    [Fact]
    public void 読み直しても休業日は増えない()
    {
        using var test = TestWorkspace.Create();

        test.Workspace.WriteWorkingDayEvents();
        var before = ClosedDays(test).Count;

        test.Workspace.WriteWorkingDayEvents();

        Assert.Equal(before, ClosedDays(test).Count);
    }

    [Fact]
    public void 休業日は文字として出さずマスの色で示す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        var main = new MainViewModel(test.Workspace, today: Closed[0]);

        // 予定の並びにも日付の行にも出さない。Google カレンダー側では文字で見える
        Assert.DoesNotContain(main.SelectedDay.Events, e => e.Title == CalendarWorkspace.ClosedDayTitle);
        Assert.DoesNotContain(CalendarWorkspace.ClosedDayTitle, main.SelectedDay.Milestones.Select(m => m.Name));

        // 面は沈む
        Assert.True(main.Month.Cells.Single(c => c.Date == Closed[0]).IsDimmed);
    }

    [Fact]
    public void 取り込んだ期間の外の休業日は残す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        Assert.NotEmpty(ClosedDays(test));

        // 合成のサンプルは 2023〜2025 年で、2026年9月とは期間が重ならない。
        // 合わせた期間（2023〜2026）で見ると、間の隙間まで休業日にしてしまう
        using var file = SampleFile();
        test.Workspace.ImportWorkingDays(file);

        Assert.Equal(Closed, ClosedDays(test).Select(e => e.Date).OrderBy(d => d));
    }

    [Fact]
    public void 起動しただけでは休業日を書かない()
    {
        // 頼まれていないのに、何百件もの予定とカレンダーができるのは行き過ぎ
        using var test = TestWorkspace.Create(withMilestones: true);

        Assert.Empty(ClosedDays(test));
    }

    // ------------------------------------------------------------------
    // 休日・祝日に稼働する日は「特別出勤」として入れる
    //
    // 暦だけ見ていると休みだと思って予定を入れそこなう。休業日より見落としたくない。
    // 呼び名は旧 inaCalendar に合わせてある
    // ------------------------------------------------------------------

    private static IReadOnlyList<CalendarEvent> OpenDays(TestWorkspace test) =>
        test.Workspace.Events.All().Where(e => CalendarWorkspace.IsOpenDayId(e.Id)).ToArray();

    [Fact]
    public void 土曜に稼働するなら特別出勤として入れる()
    {
        // 9月26日（土）まで稼働する月にする
        var days = Enumerable.Range(1, 30)
            .Select(d => new DateOnly(2026, 9, d))
            .Where(d => d.DayOfWeek != DayOfWeek.Sunday)
            .ToArray();

        using var test = TestWorkspace.Create(withWorkingDays: false);
        Save(test, days);

        test.Workspace.WriteWorkingDayEvents();

        var open = OpenDays(test);

        Assert.All(open, e => Assert.Equal(CalendarWorkspace.OpenDayTitle, e.Title));
        Assert.Contains(new DateOnly(2026, 9, 26), open.Select(e => e.Date));

        // 平日に稼働するのはふつうのこと。入れると埋もれる
        Assert.DoesNotContain(new DateOnly(2026, 9, 24), open.Select(e => e.Date));
    }

    [Fact]
    public void 祝日に稼働するなら特別出勤として入れる()
    {
        var holiday = new DateOnly(2026, 9, 21);
        var days = Enumerable.Range(1, 30)
            .Select(d => new DateOnly(2026, 9, d))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToArray();

        using var test = TestWorkspace.Create(
            withWorkingDays: false,
            holidays: new Dictionary<DateOnly, string> { [holiday] = "敬老の日" });

        Save(test, days);
        test.Workspace.WriteWorkingDayEvents();

        Assert.Contains(holiday, OpenDays(test).Select(e => e.Date));
    }

    [Fact]
    public void 休日に稼働しないなら特別出勤は入れない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.WriteWorkingDayEvents();

        // 土日は稼働しない月なので、特別出勤の印は付かない
        Assert.Empty(OpenDays(test));
    }

    [Fact]
    public void 特別出勤も文字として出さずマスの色で示す()
    {
        var days = Enumerable.Range(1, 30)
            .Select(d => new DateOnly(2026, 9, d))
            .Where(d => d.DayOfWeek != DayOfWeek.Sunday)
            .ToArray();

        using var test = TestWorkspace.Create(withWorkingDays: false);
        Save(test, days);
        test.Workspace.WriteWorkingDayEvents();

        var saturday = new DateOnly(2026, 9, 26);
        var main = new MainViewModel(test.Workspace, today: saturday);

        Assert.DoesNotContain(main.SelectedDay.Events, e => e.Title == CalendarWorkspace.OpenDayTitle);
        Assert.DoesNotContain(CalendarWorkspace.OpenDayTitle, main.SelectedDay.Milestones.Select(m => m.Name));

        // 土曜だが稼働するので面を起こす
        Assert.True(main.Month.Cells.Single(c => c.Date == saturday).IsWorkingDayLit);
    }

    /// <summary>稼働日を直に入れる。取り込みを通さずに並びを決めたいとき。</summary>
    private static void Save(TestWorkspace test, IReadOnlyList<DateOnly> days)
    {
        test.Workspace.WorkingDayStore.Save(WorkingDayCalendar.Create(
            days, days[0], days[^1], [], null, null));

        test.Workspace.ReloadWorkingDays();
    }

    [Fact]
    public void すでに同じ印があれば作らない()
    {
        using var test = TestWorkspace.Create();

        // 旧 inaCalendar が Google に書き込んだ分が、同期で降りてきている体
        var ina = test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "google:abc", Title = CalendarWorkspace.ClosedDayTitle,
            Date = Closed[0], CalendarId = ina.Id, Source = "google",
        });

        test.Workspace.WriteWorkingDayEvents();

        // こちらでも作ると、次の同期で Google 側に同じ予定が2つ並ぶ
        Assert.DoesNotContain(Closed[0], ClosedDays(test).Select(e => e.Date));

        // 印の無い日には今までどおり作る
        Assert.Contains(Closed[1], ClosedDays(test).Select(e => e.Date));

        // よそから来た分だけでも面は沈む
        var main = new MainViewModel(test.Workspace, today: Closed[0]);
        Assert.True(main.Month.Cells.Single(c => c.Date == Closed[0]).IsDimmed);
    }
}
