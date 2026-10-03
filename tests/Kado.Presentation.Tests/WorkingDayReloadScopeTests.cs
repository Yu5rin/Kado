using Kado.Core.WorkingDays;
using Kado.Data.Models;
using Kado.Presentation;

namespace Kado.Presentation.Tests;

/// <summary>
/// 同期のたびに走る実働日の読み直しは、実働日カレンダー（「Kado」の印）の予定だけを読む。
/// 結果（実働日・範囲）は、全予定を読んで絞っていた今までと同じ。
/// </summary>
public class WorkingDayReloadScopeTests
{
    private static CalendarEvent Mark(string id, string title, DateOnly date, string calendarId) => new()
    {
        Id = id, Title = title, Date = date, CalendarId = calendarId, Source = "google",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>今までの組み立て方（全件を読んでから絞る）。期待値を作るのに使う。</summary>
    private static WorkingDayCalendar Expected(CalendarWorkspace workspace)
    {
        var ids = workspace.WorkingDayCalendars().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        return WorkingDayMarks.Rebuild(
            workspace.Events.All().Where(e => e.CalendarId is { } id && ids.Contains(id)),
            workspace.Holidays);
    }

    [Fact]
    public void 印の予定だけで組み立てた結果は_全件から絞った結果と同じ()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var workspace = test.Workspace;

        var kado = workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        var other = workspace.CreateCalendar("仕事");

        // 印（平日の休業日と、土曜の特別出勤）
        workspace.AddEvent(Mark("m1", CalendarWorkspace.ClosedDayTitle, new DateOnly(2026, 9, 24), kado.Id));
        workspace.AddEvent(Mark("m2", CalendarWorkspace.OpenDayTitle, new DateOnly(2026, 10, 3), kado.Id));

        // 別のカレンダーの予定。題が「休業日」でも、印としては数えない
        workspace.AddEvent(Mark("o1", CalendarWorkspace.ClosedDayTitle, new DateOnly(2026, 12, 25), other.Id));
        for (var i = 0; i < 50; i++)
        {
            workspace.AddEvent(Mark($"w{i}", $"打ち合わせ{i}", new DateOnly(2026, 9, 1).AddDays(i), other.Id));
        }

        workspace.ReloadWorkingDays();

        var expected = Expected(workspace);
        var actual = workspace.WorkingDays;

        Assert.Equal(expected.Days, actual.Days);
        Assert.Equal(expected.RangeStart, actual.RangeStart);
        Assert.Equal(expected.RangeEnd, actual.RangeEnd);

        // 別のカレンダーの「休業日」は、範囲も日数も広げていない
        Assert.Equal(new DateOnly(2026, 9, 1), actual.RangeStart);
        Assert.Equal(new DateOnly(2026, 10, 31), actual.RangeEnd);
        Assert.DoesNotContain(new DateOnly(2026, 12, 25), actual.Days);
        Assert.False(actual.IsWorkingDay(new DateOnly(2026, 9, 24)));
        Assert.True(actual.IsWorkingDay(new DateOnly(2026, 10, 3)));
    }

    [Fact]
    public void 実働日カレンダーが無ければ空のまま()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var workspace = test.Workspace;

        var other = workspace.CreateCalendar("仕事");
        workspace.AddEvent(Mark("o1", CalendarWorkspace.ClosedDayTitle, new DateOnly(2026, 9, 24), other.Id));

        workspace.ReloadWorkingDays();

        Assert.Equal(WorkingDayCalendar.Empty.Days, workspace.WorkingDays.Days);
        Assert.Null(workspace.WorkingDays.RangeStart);
    }

    [Fact]
    public void 同期で印が増えたあとの読み直しでも_実働日が更新される()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var workspace = test.Workspace;
        var kado = workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);

        workspace.ReloadWorkingDays();
        Assert.Null(workspace.WorkingDays.RangeStart);

        // 同期は別の接続で書く。ここでは直に書き込んで、読み直しだけを呼ぶ
        workspace.Events.Upsert(Mark("m1", CalendarWorkspace.ClosedDayTitle, new DateOnly(2026, 9, 24), kado.Id));
        workspace.ReloadWorkingDays();

        Assert.Equal(new DateOnly(2026, 9, 1), workspace.WorkingDays.RangeStart);
        Assert.False(workspace.WorkingDays.IsWorkingDay(new DateOnly(2026, 9, 24)));
        Assert.True(workspace.WorkingDays.IsWorkingDay(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void 読み直しで読むのは実働日カレンダーの予定だけ()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var workspace = test.Workspace;

        var kado = workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        var other = workspace.CreateCalendar("仕事");

        workspace.AddEvent(Mark("m1", CalendarWorkspace.ClosedDayTitle, new DateOnly(2026, 9, 24), kado.Id));
        for (var i = 0; i < 200; i++)
        {
            workspace.AddEvent(Mark($"w{i}", $"打ち合わせ{i}", new DateOnly(2026, 9, 1).AddDays(i % 60), other.Id));
        }

        // events を読むたびに数える。全件を読めば 201 になり、絞れば 1 になる
        var reads = 0;
        test.Connection.CreateFunction("kado_read_probe", () => { reads++; return 1; });

        using (var create = test.Connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TEMP VIEW events AS SELECT * FROM main.events WHERE kado_read_probe() = 1;";
            create.ExecuteNonQuery();
        }

        try
        {
            workspace.ReloadWorkingDays();
        }
        finally
        {
            using var drop = test.Connection.CreateCommand();
            drop.CommandText = "DROP VIEW IF EXISTS temp.events;";
            drop.ExecuteNonQuery();
        }

        Assert.True(reads > 0, "読んだ回数を数えられていない");
        Assert.True(reads < 20, $"実働日カレンダーの予定より多く読んでいる（{reads} 行）");
    }
}
