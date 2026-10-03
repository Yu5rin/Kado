using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// Kado では表せない繰り返し（RDATE など）の予定。
/// <para>
/// こちらでは <c>Recurrence</c> が空で持つが、向こうでは繰り返しの予定。ふつうの単発の予定と
/// 同じに扱ってドラッグで開始日を動かすと、系列の開始がずれる。繰り返しの予定と同じに止める。
/// </para>
/// </summary>
public class UnrepresentableRecurrenceTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);
    private static readonly DateOnly Tomorrow = new(2026, 9, 25);

    private const string RdateRaw = """
        {"id":"g1","summary":"不定期の点検","recurrence":["RDATE;VALUE=DATE:20260924,20261008"],
         "start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}
        """;

    private static CalendarEvent Held() => new()
    {
        Id = "h1", Title = "不定期の点検", Date = Today, CalendarId = "cal-a",
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        GoogleEventId = "g1", GoogleCalendarId = "cal-a", GoogleRaw = RdateRaw, Source = "google",
    };

    private static MainViewModel Create(TestWorkspace test) => new(test.Workspace, Today);

    [Fact]
    public void 表せない繰り返しの予定はドラッグで動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Held());

        var main = Create(test);

        Assert.False(main.MoveEventTo("h1", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("h1")!.Date);

        // 繰り返しの予定と同じ止め方と文言
        Assert.Equal("繰り返しの予定は編集画面から変えてください", main.StatusMessage);
    }

    [Fact]
    public void 表せない繰り返しの予定は複製も時刻を変えるドラッグも終日にするドラッグもできない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Held());

        var main = Create(test);

        Assert.False(main.MoveEventTo("h1", Tomorrow, copy: true));
        Assert.False(main.MoveEventToTime("h1", Tomorrow, new TimeOnly(14, 0)));
        Assert.False(main.MoveEventToAllDay("h1", Tomorrow));
        Assert.Single(test.Workspace.Events.All());
    }

    [Fact]
    public void 表せない繰り返しの予定を削除するとすべての回だと分かる文言になる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Held());

        var main = Create(test);
        main.DeleteEventCommand.Execute(Assert.Single(main.SelectedDay.Events));

        Assert.Equal("繰り返しの予定をすべての回、削除しました", main.StatusMessage);
    }

    [Fact]
    public void 繰り返しを外した予定は今までどおり動かせる()
    {
        using var test = TestWorkspace.Create();

        // 控えに繰り返しの行が無い（外してある）
        test.Workspace.AddEvent(Held() with
        {
            GoogleRaw = """{"id":"g1","summary":"点検","start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}""",
        });

        Assert.True(Create(test).MoveEventTo("h1", Tomorrow));
    }

    [Fact]
    public void 編集画面は繰り返しの欄に表せない旨を出す()
    {
        var editor = new EventEditorViewModel(Held(), []);

        Assert.Equal(RecurrenceKind.Custom, editor.Recurrence);

        var option = Assert.Single(editor.RecurrenceOptions, o => o.Kind == RecurrenceKind.Custom);
        Assert.Equal("Kado では表せない繰り返し（Google で編集してください）", option.Label);
    }

    [Fact]
    public void 編集画面は繰り返しと日時を変えさせない()
    {
        var editor = new EventEditorViewModel(Held(), []);

        Assert.False(editor.CanChangeSchedule);
        Assert.NotNull(editor.ScheduleLockReason);

        editor.Recurrence = RecurrenceKind.Daily;
        editor.Date = Tomorrow;
        editor.EndDate = Tomorrow.AddDays(3);
        editor.IsAllDay = true;
        editor.StartTimeText = "13:00";
        editor.EndTimeText = "15:00";
        editor.NudgeStart(30);
        editor.NudgeEnd(30);

        Assert.Equal(RecurrenceKind.Custom, editor.Recurrence);
        Assert.Equal(Today, editor.Date);
        Assert.Equal(Today, editor.EndDate);
        Assert.False(editor.IsAllDay);
        Assert.Equal("10:00", editor.StartTimeText);
        Assert.Equal("11:00", editor.EndTimeText);
    }

    [Fact]
    public void 編集画面で題と場所とメモは直せて繰り返しと日時はそのまま保存される()
    {
        var editor = new EventEditorViewModel(Held(), [])
        {
            Title = "不定期の点検（変更）",
            Location = "第2工場",
            Note = "メモ",
        };

        var model = editor.ToModel();

        Assert.Equal("不定期の点検（変更）", model.Title);
        Assert.Equal("第2工場", model.Location);
        Assert.Equal("メモ", model.Note);

        Assert.Equal(Today, model.Date);
        Assert.Equal(new TimeOnly(10, 0), model.StartTime);
        Assert.Equal(new TimeOnly(11, 0), model.EndTime);

        // 繰り返しは空のまま。控えた生データが残るので、向こうの RDATE は無傷
        Assert.Null(model.Recurrence);
        Assert.Equal(RdateRaw, model.GoogleRaw);
    }

    [Fact]
    public void ふつうの予定は日時と繰り返しを変えられる()
    {
        var editor = new EventEditorViewModel(
            Held() with { GoogleRaw = """{"id":"g1","summary":"点検"}""" }, []);

        Assert.True(editor.CanChangeSchedule);
        Assert.Null(editor.ScheduleLockReason);

        editor.Date = Tomorrow;
        editor.Recurrence = RecurrenceKind.Daily;

        Assert.Equal(Tomorrow, editor.Date);
        Assert.Equal(RecurrenceKind.Daily, editor.Recurrence);
    }
}
