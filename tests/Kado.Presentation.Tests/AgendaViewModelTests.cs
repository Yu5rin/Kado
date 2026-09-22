using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 一覧（アジェンダ）ビュー。
/// <para>
/// 予定のない日は連続分を1行に畳む。消してしまうと間がどれだけ空いているのか
/// 分からなくなり、実働日の感覚が飛ぶ。
/// </para>
/// </summary>
public class AgendaViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static AgendaViewModel Create(TestWorkspace test)
    {
        var sources = new SourceListsViewModel(test.Workspace);

        return new AgendaViewModel(test.Workspace, D(2026, 9, 20), sources);
    }

    private static void Add(TestWorkspace test, string id, string title, DateOnly date,
        TimeOnly? start = null) =>
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = id, Title = title, Date = date, StartTime = start,
            EndTime = start is { } s ? s.AddHours(1) : null,
        });

    [Fact]
    public void 予定のある日だけ行になる()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 24));

        var vm = Create(test);

        var day = Assert.Single(vm.Rows, r => !r.IsGap);
        Assert.Equal(D(2026, 9, 24), day.Date);
        Assert.Equal("棚卸", Assert.Single(day.Events).Title);
    }

    [Fact]
    public void 予定のない日は連続分をまとめて一行にする()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 24));

        var vm = Create(test);

        // 今日（9/20）から 9/23 が1行、9/24 が1行。
        // 範囲は最後の予定で終わるので、うしろに空の行は出ない
        Assert.Equal(2, vm.Rows.Count);

        var before = vm.Rows[0];
        Assert.True(before.IsGap);
        Assert.Equal(D(2026, 9, 20), before.Date);
        Assert.Equal(D(2026, 9, 23), before.LastDate);
        Assert.Equal(4, before.SkippedDays);
        Assert.Equal("予定なし 4日", before.GapText);

        Assert.False(vm.Rows[1].IsGap);
        Assert.Equal(D(2026, 9, 24), vm.Rows[1].Date);
    }

    [Fact]
    public void 予定と予定のあいだも畳む()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 20));
        Add(test, "e2", "会議", D(2026, 9, 28));

        var vm = Create(test);

        Assert.Equal(3, vm.Rows.Count);

        var between = vm.Rows[1];
        Assert.True(between.IsGap);
        Assert.Equal(D(2026, 9, 21), between.Date);
        Assert.Equal(D(2026, 9, 27), between.LastDate);
        Assert.Equal("予定なし 7日", between.GapText);
    }

    [Fact]
    public void 畳んだ行も日付の連続性を保つ()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 22));
        Add(test, "e2", "会議", D(2026, 9, 25));

        var vm = Create(test);

        // 先頭から末尾まで、途切れずに並ぶ
        var expected = vm.From;
        foreach (var row in vm.Rows)
        {
            Assert.Equal(expected, row.Date);
            expected = row.LastDate.AddDays(1);
        }

        Assert.Equal(vm.To.AddDays(1), expected);
    }

    [Fact]
    public void 一日だけ空いたら日数を書かない()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 20));
        Add(test, "e2", "会議", D(2026, 9, 22));

        var vm = Create(test);

        Assert.Equal("予定なし", vm.Rows[1].GapText);
        Assert.Equal(1, vm.Rows[1].SkippedDays);
    }

    [Fact]
    public void 期限のあるタスクも並ぶ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "提出", Due = D(2026, 9, 24) });

        var vm = Create(test);

        var day = Assert.Single(vm.Rows, r => !r.IsGap);
        Assert.Equal("提出", Assert.Single(day.Tasks).Title);
    }

    [Fact]
    public void 予定は時刻の順に並ぶ()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "午後", D(2026, 9, 24), new TimeOnly(14, 0));
        Add(test, "e2", "終日", D(2026, 9, 24));
        Add(test, "e3", "朝", D(2026, 9, 24), new TimeOnly(9, 0));

        var vm = Create(test);

        var day = Assert.Single(vm.Rows, r => !r.IsGap);
        Assert.Equal(["終日", "朝", "午後"], day.Events.Select(e => e.Title));
    }

    [Fact]
    public void 実働日の通し番号と祝日名を添える()
    {
        using var test = TestWorkspace.Create(
            holidays: new Dictionary<DateOnly, string> { [D(2026, 9, 21)] = "敬老の日" });

        Add(test, "e1", "棚卸", D(2026, 9, 24));
        Add(test, "e2", "休みの日の用事", D(2026, 9, 21));

        var vm = Create(test);

        var holiday = vm.Rows.Single(r => r.Date == D(2026, 9, 21));
        Assert.Equal("敬老の日", holiday.HolidayName);
        Assert.True(holiday.IsSundayLike);
        Assert.Null(holiday.WorkingDayText);

        var workday = vm.Rows.Single(r => r.Date == D(2026, 9, 24));
        Assert.Equal("実働 15日目", workday.WorkingDayText);
    }

    [Fact]
    public void 今日の行が分かる()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 20));

        var vm = Create(test);

        Assert.True(vm.Rows.Single(r => r.Date == D(2026, 9, 20)).IsToday);
    }

    [Fact]
    public void 何も無ければ空として扱う()
    {
        using var test = TestWorkspace.Create();

        var vm = Create(test);

        // 畳んだ行だけなら、案内を出す
        Assert.True(vm.IsEmpty);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public void 持っているぶんを全部出す()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "少し前", D(2025, 10, 1));
        Add(test, "e2", "少し先", D(2027, 5, 20));

        var vm = Create(test);

        // 区切ると、探しているものが隣の期間にあることになって使いにくい
        Assert.Equal(D(2025, 10, 1), vm.From);
        Assert.Equal(D(2027, 5, 20), vm.To);

        Assert.Contains(vm.Rows, r => r.Date == D(2025, 10, 1));
        Assert.Contains(vm.Rows, r => r.Date == D(2027, 5, 20));
    }

    [Fact]
    public void データが今日より後ろだけでも今日の行は出す()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "ずっと先", D(2027, 4, 1));

        var vm = Create(test);

        // いまどこにいるのか分からないと、上下どちらへ送ればよいか決められない
        Assert.Equal(D(2026, 9, 20), vm.From);
        Assert.NotNull(vm.TodayRow);
    }

    [Fact]
    public void 一件も無ければ今日のまわりを出す()
    {
        using var test = TestWorkspace.Create();

        var vm = Create(test);

        Assert.Equal(D(2026, 9, 20).AddDays(-AgendaViewModel.EmptySpanDays), vm.From);
        Assert.Equal(D(2026, 9, 20).AddDays(AgendaViewModel.EmptySpanDays), vm.To);
    }

    [Fact]
    public void 遠すぎるデータでは範囲を切る()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "まぎれこみ", D(1990, 1, 1));

        var vm = Create(test);

        // 繰り返しの予定はこの期間ぶんすべて展開される。広げすぎると目に見えて重くなる
        Assert.True(vm.From >= D(2024, 9, 20));
    }

    [Fact]
    public void 選んだ日を含む行に印が付く()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 24));

        var vm = Create(test);

        vm.SelectedDate = D(2026, 9, 24);
        Assert.True(vm.Rows.Single(r => r.Date == D(2026, 9, 24)).IsSelected);

        // 畳んだ行の中の日を選んでも、その行が光る
        vm.SelectedDate = D(2026, 9, 22);
        Assert.True(vm.Rows[0].IsSelected);
        Assert.False(vm.Rows.Single(r => r.Date == D(2026, 9, 24)).IsSelected);
    }

    [Fact]
    public void その日を含む行を引ける()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 24));

        var vm = Create(test);

        Assert.Equal(D(2026, 9, 24), vm.RowOn(D(2026, 9, 24))?.Date);

        // 畳んだ行の中の日でも引ける
        Assert.True(vm.RowOn(D(2026, 9, 22))?.IsGap);
    }

    [Fact]
    public void 畳んだ行の日付は範囲で出す()
    {
        using var test = TestWorkspace.Create();
        Add(test, "e1", "棚卸", D(2026, 9, 24));

        var vm = Create(test);

        Assert.Equal("9月20日 〜 9月23日", vm.Rows[0].DateText);
        Assert.Equal("9月24日（木）", vm.Rows[1].DateText);
    }

    [Fact]
    public void 日付の行のマイルストーンは予定として出さない()
    {
        using var test = TestWorkspace.Create(withMilestones: true);

        var vm = Create(test);

        // マイルストーンは日付の行に出すもの。一覧に予定として混ぜると二重に見える
        Assert.All(vm.Rows, r => Assert.All(r.Events,
            e => Assert.False(CalendarWorkspace.IsMilestoneMark(e.Scheduled.Source))));
    }
}
