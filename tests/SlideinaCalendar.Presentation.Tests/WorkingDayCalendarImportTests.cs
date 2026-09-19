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

        var written = test.Workspace.Events.All()
            .Where(e => e.CalendarId == calendar.Id)
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
}
