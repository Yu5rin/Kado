using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// ドラッグで別の日へ移す。Ctrl を押しながらなら複製。
/// <para>受け口は表示側にあるが、動かすのは ViewModel なのでここで確かめる。</para>
/// </summary>
public class DragMoveTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);
    private static readonly DateOnly Tomorrow = new(2026, 9, 25);

    private static MainViewModel Create(TestWorkspace test) => new(test.Workspace, Today);

    private static CalendarEvent Event(string id, DateOnly date, DateOnly? end = null) => new()
    {
        Id = id, Title = "打ち合わせ", Date = date, EndDate = end,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
    };

    [Fact]
    public void 予定を別の日へ移せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var main = Create(test);
        Assert.True(main.MoveEventTo("e1", Tomorrow));

        Assert.Equal(Tomorrow, test.Workspace.Events.Find("e1")!.Date);
    }

    [Fact]
    public void 移しても時刻は変わらない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Create(test).MoveEventTo("e1", Tomorrow);

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(new TimeOnly(10, 0), moved.StartTime);
        Assert.Equal(new TimeOnly(11, 0), moved.EndTime);
    }

    [Fact]
    public void 期間のある予定は長さを保つ()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today, Today.AddDays(2)));

        Create(test).MoveEventTo("e1", Tomorrow);

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(Tomorrow, moved.Date);
        Assert.Equal(Tomorrow.AddDays(2), moved.EndDate);
    }

    [Fact]
    public void 複製すると元が残る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Create(test).MoveEventTo("e1", Tomorrow, copy: true);

        Assert.Equal(Today, test.Workspace.Events.Find("e1")!.Date);
        Assert.Equal(2, test.Workspace.Events.All().Count(e => e.Title == "打ち合わせ"));
        Assert.Contains(test.Workspace.Events.All(), e => e.Date == Tomorrow && e.Id != "e1");
    }

    [Fact]
    public void 複製は相手側の識別子を引き継がない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today) with
        {
            GoogleEventId = "g1", GoogleRaw = "{}", Source = "google",
        });

        Create(test).MoveEventTo("e1", Tomorrow, copy: true);

        // 引き継ぐと、次の同期で元の予定のほうが書き換わる
        var copy = test.Workspace.Events.All().Single(e => e.Id != "e1");
        Assert.Null(copy.GoogleEventId);
        Assert.Null(copy.GoogleRaw);
    }

    [Fact]
    public void 実働日データの印は動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:2026-09-24", Title = CalendarWorkspace.ClosedDayTitle,
            Date = Today, Source = CalendarWorkspace.WorkingDaySource,
        });

        Assert.False(Create(test).MoveEventTo("closedday:2026-09-24", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("closedday:2026-09-24")!.Date);
    }

    [Fact]
    public void 同じ日に落としても何も起きない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.False(Create(test).MoveEventTo("e1", Today));
    }

    [Fact]
    public void 移したあとは元に戻せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var main = Create(test);
        main.MoveEventTo("e1", Tomorrow);
        main.UndoCommand.Execute(null);

        Assert.Equal(Today, test.Workspace.Events.Find("e1")!.Date);
    }

    [Fact]
    public void タスクの期限も移せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = Today });

        Assert.True(Create(test).MoveTaskTo("t1", Tomorrow));
        Assert.Equal(Tomorrow, test.Workspace.Tasks.Find("t1")!.Due);
    }

    [Fact]
    public void タスクも複製できる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "資料作成", Due = Today });

        Create(test).MoveTaskTo("t1", Tomorrow, copy: true);

        Assert.Equal(Today, test.Workspace.Tasks.Find("t1")!.Due);
        Assert.Contains(test.Workspace.Tasks.All(), t => t.Due == Tomorrow && t.Id != "t1");
    }

    [Fact]
    public void 無い予定を移そうとしても落ちない()
    {
        using var test = TestWorkspace.Create();
        var main = Create(test);

        Assert.False(main.MoveEventTo("ない", Tomorrow));
        Assert.False(main.MoveEventTo(null, Tomorrow));
        Assert.False(main.MoveTaskTo("ない", Tomorrow));
    }

    /// <summary>メールから起こされた予約。Google 側で内容を変えられない。</summary>
    private static CalendarEvent Locked(string id, DateOnly date) => new()
    {
        Id = id, Title = "HotPepper Beauty のサロン予約", Date = date,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        Source = "google", GoogleEventId = "g1",
        GoogleRaw = """{"id":"g1","summary":"HotPepper Beauty のサロン予約","eventType":"fromGmail","locked":true}""",
    };

    [Fact]
    public void 向こうで変えられない予定は動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Locked("g-local", Today));

        var main = Create(test);

        Assert.False(main.MoveEventTo("g-local", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("g-local")!.Date);
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void 向こうで変えられない予定でも複製はできる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Locked("g-local", Today));

        // 複製は新しい予定になるので、こちらの好きに扱える
        Assert.True(Create(test).MoveEventTo("g-local", Tomorrow, copy: true));
        Assert.Contains(test.Workspace.Events.All(), e => e.Date == Tomorrow && e.Id != "g-local");
    }

    [Fact]
    public void 向こうで変えられない予定は編集画面を開かない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Locked("g-local", Today));

        var editors = new FakeEditorPresenter();
        var main = new MainViewModel(test.Workspace, Today, editors: editors);
        var chip = main.Month.Cells.Single(c => c.Date == Today).Events.Single();

        main.EditChipCommand.Execute(chip);

        // 開けてしまうと、直せたように見えて向こうには伝わらない
        Assert.Null(editors.LastEventEditor);
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void ふつうの予定なら編集画面が開く()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var editors = new FakeEditorPresenter();
        var main = new MainViewModel(test.Workspace, Today, editors: editors);
        var chip = main.Month.Cells.Single(c => c.Date == Today).Events.Single();

        main.EditChipCommand.Execute(chip);

        Assert.NotNull(editors.LastEventEditor);
    }

    [Fact]
    public void ふつうの予定は変えられないとは見なさない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today) with
        {
            Source = "google", GoogleEventId = "g2",
            GoogleRaw = """{"id":"g2","summary":"打ち合わせ"}""",
        });

        Assert.False(MainViewModel.IsLocked(test.Workspace.Events.Find("e1")!));
        Assert.True(Create(test).MoveEventTo("e1", Tomorrow));
    }

    [Fact]
    public void 時刻ごと別の日へ移せる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));   // 10:00〜11:00

        Assert.True(Create(test).MoveEventToTime("e1", Tomorrow, new TimeOnly(14, 30)));

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(Tomorrow, moved.Date);
        Assert.Equal(new TimeOnly(14, 30), moved.StartTime);

        // 長さは保つ。1時間の予定は移しても1時間
        Assert.Equal(new TimeOnly(15, 30), moved.EndTime);
    }

    [Fact]
    public void 同じ日でも時刻が変われば動かしたことになる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.True(Create(test).MoveEventToTime("e1", Today, new TimeOnly(16, 0)));
        Assert.Equal(new TimeOnly(16, 0), test.Workspace.Events.Find("e1")!.StartTime);
    }

    [Fact]
    public void 同じ日の同じ時刻なら何も起きない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.False(Create(test).MoveEventToTime("e1", Today, new TimeOnly(10, 0)));
    }

    [Fact]
    public void 終日の予定を時間軸に落とすと1時間の予定になる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "全社会議", Date = Today });

        Create(test).MoveEventToTime("e1", Today, new TimeOnly(9, 0));

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(new TimeOnly(9, 0), moved.StartTime);
        Assert.Equal(new TimeOnly(10, 0), moved.EndTime);
    }

    [Fact]
    public void 終日レーンに落とすと時刻が外れる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.True(Create(test).MoveEventToAllDay("e1", Tomorrow));

        var moved = test.Workspace.Events.Find("e1")!;
        Assert.Equal(Tomorrow, moved.Date);
        Assert.Null(moved.StartTime);
        Assert.Null(moved.EndTime);
    }

    [Fact]
    public void 仕様期限などのラベルも動かせる()
    {
        using var test = TestWorkspace.Create();
        var ina = test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = CalendarWorkspace.WorkingDaySource + ":2026-09-24:仕様期限",
            Title = "仕様期限", Date = Today, CalendarId = ina.Id,
            Source = CalendarWorkspace.WorkingDaySource,
        });

        var id = CalendarWorkspace.WorkingDaySource + ":2026-09-24:仕様期限";
        Assert.True(Create(test).MoveEventTo(id, Tomorrow));
        Assert.Equal(Tomorrow, test.Workspace.Events.Find(id)!.Date);
    }

    [Fact]
    public void 休業日と特別出勤は動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "closedday:2026-09-24", Title = CalendarWorkspace.ClosedDayTitle,
            Date = Today, Source = CalendarWorkspace.WorkingDaySource,
        });

        var main = Create(test);

        // マスの色を決める印で、識別子にその日付が入っている
        Assert.False(main.MoveEventTo("closedday:2026-09-24", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("closedday:2026-09-24")!.Date);
    }

    /// <summary>毎週月曜の繰り返し予定。</summary>
    private static CalendarEvent Recurring(string id, DateOnly date) => new()
    {
        Id = id, Title = "定例会議", Date = date,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        Recurrence = "FREQ=WEEKLY;BYDAY=MO",
    };

    [Fact]
    public void 繰り返しの予定はドラッグで動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1", Today));

        var main = Create(test);

        Assert.False(main.MoveEventTo("r1", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("r1")!.Date);
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void 繰り返しの予定は複製もドラッグではできない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1", Today));

        var main = Create(test);

        // 複製でも規則を引き継ぐと複製先で同じ問題が起きるので、こちらも止める
        Assert.False(main.MoveEventTo("r1", Tomorrow, copy: true));
        Assert.Single(test.Workspace.Events.All());
    }

    [Fact]
    public void 繰り返しの予定は時刻を変えるドラッグも終日にするドラッグも動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1", Today));

        var main = Create(test);

        Assert.False(main.MoveEventToTime("r1", Tomorrow, new TimeOnly(14, 0)));
        Assert.False(main.MoveEventToAllDay("r1", Tomorrow));
    }

    [Fact]
    public void 繰り返しでない予定は今までどおりドラッグで動かせる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.True(Create(test).MoveEventTo("e1", Tomorrow));
    }

    [Fact]
    public void 繰り返しの予定を削除するとすべての回だと分かる文言になる()
    {
        // Today（2026-09-24）は木曜なので、月曜始まりの回は同じ月内の月曜（2026-09-21）
        var monday = new DateOnly(2026, 9, 21);

        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1", monday));

        var main = Create(test);
        var chip = main.Month.Cells.Single(c => c.Date == monday).Events.Single();
        main.DeleteChipCommand.Execute(chip);

        Assert.Null(test.Workspace.Events.Find("r1"));
        Assert.Contains("すべての回", main.StatusMessage);
    }

    [Fact]
    public void 遅い時刻に移しても画面から消えない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));   // 10:00〜11:00

        var main = Create(test);
        main.MoveEventToTime("e1", Today, new TimeOnly(23, 30));

        var moved = test.Workspace.Events.Find("e1")!;

        // 日をまたぐと終わりが始まりより前になり、時間軸に置けなくなる
        Assert.True(moved.EndTime > moved.StartTime);
        Assert.Equal(new TimeOnly(23, 59), moved.EndTime);
    }

    // ------------------------------------------------------------------
    // 読み取り専用のカレンダー
    //
    // 送信は止まるので Google 側は無傷だが、黙って編集・削除・ドラッグをさせると
    // こちらだけ変わって食い違い、しかも何も知らされないのでは不親切
    // ------------------------------------------------------------------

    private static CalendarSource ReadOnlyCalendar(string id = "cal-ro") => new()
    {
        Id = id, Summary = "共有カレンダー",
        GoogleRaw = $$"""{"id":"{{id}}","accessRole":"reader"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void 読み取り専用カレンダーの予定はドラッグで動かせない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnlyCalendar());
        test.Workspace.AddEvent(Event("e1", Today) with { CalendarId = "cal-ro" });

        var main = Create(test);

        Assert.False(main.MoveEventTo("e1", Tomorrow));
        Assert.Equal(Today, test.Workspace.Events.Find("e1")!.Date);
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void 読み取り専用カレンダーの予定は複製も止める()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnlyCalendar());
        test.Workspace.AddEvent(Event("e1", Today) with { CalendarId = "cal-ro" });

        // 複製で入れても、その複製先が書けないまま残る。編集で直すこともできないので、
        // 複製かどうかに関わらず止める
        Assert.False(Create(test).MoveEventTo("e1", Tomorrow, copy: true));
        Assert.Single(test.Workspace.Events.All());
    }

    [Fact]
    public void 読み取り専用カレンダーの予定は編集画面を開かない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnlyCalendar());
        test.Workspace.AddEvent(Event("e1", Today) with { CalendarId = "cal-ro" });

        var editors = new FakeEditorPresenter();
        var main = new MainViewModel(test.Workspace, Today, editors: editors);
        var chip = main.Month.Cells.Single(c => c.Date == Today).Events.Single();

        main.EditChipCommand.Execute(chip);

        Assert.Null(editors.LastEventEditor);
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void 読み取り専用カレンダーの予定は削除もできない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(ReadOnlyCalendar());
        test.Workspace.AddEvent(Event("e1", Today) with { CalendarId = "cal-ro" });

        var main = Create(test);
        var chip = main.Month.Cells.Single(c => c.Date == Today).Events.Single();
        main.DeleteChipCommand.Execute(chip);

        Assert.NotNull(test.Workspace.Events.Find("e1"));
        Assert.NotNull(main.StatusMessage);
    }

    [Fact]
    public void 書けるカレンダーの予定は今までどおり扱える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "cal-rw", Summary = "自分のカレンダー",
            GoogleRaw = """{"id":"cal-rw","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });
        test.Workspace.AddEvent(Event("e1", Today) with { CalendarId = "cal-rw" });

        Assert.True(Create(test).MoveEventTo("e1", Tomorrow));
    }

    [Fact]
    public void カレンダーに属さない予定は読み取り専用扱いにしない()
    {
        // CalendarId が無い予定（手で入れた、まだどのカレンダーにも属さない）まで
        // 巻き込むと、ふつうの操作ができなくなる
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        Assert.True(Create(test).MoveEventTo("e1", Tomorrow));
    }

    [Fact]
    public void 移した予定は週ビューに出る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("e1", Today));

        var settings = new Settings.AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, Today, settings: settings);

        main.MoveEventToTime("e1", Today, new TimeOnly(23, 30));

        var column = main.Week.Days.Single(d => d.Date == Today);
        Assert.Contains(column.Blocks, b => b.Id == "e1");
    }
}
