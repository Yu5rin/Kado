using Kado.Data.Models;
using Kado.Presentation.Menus;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 日付・空き時間の右クリックメニュー（この日に予定・タスクを追加／この日・この週・この月を見る）。
/// 置く場所ごとに行の型が違う（月のマス・週の列・年の日・一覧の行・右ペイン）ので、
/// どれからでも日付が読めること、今いるビューと同じものを出さないことを見る。
/// </summary>
public class DayContextMenuTests : IDisposable
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly DateOnly Today = D(2026, 10, 6);

    private readonly TestWorkspace _test = TestWorkspace.Create();
    private readonly FakeEditorPresenter _editors = new();
    private readonly MainViewModel _vm;

    public DayContextMenuTests() => _vm = new MainViewModel(_test.Workspace, Today, editors: _editors);

    public void Dispose() => _test.Dispose();

    // ------------------------------------------------------------------
    // 日付の読み取り
    // ------------------------------------------------------------------

    [Fact]
    public void どの置き場所の行からでも日付を読める()
    {
        var cell = _vm.Month.Cells.Single(c => c.Date == Today);
        Assert.Equal(Today, DayTarget.From(cell)!.Date);

        Assert.Equal(Today, DayTarget.From(_vm.SelectedDay)!.Date);

        _vm.CurrentView = CalendarView.Week;
        var column = _vm.Week.Days.Single(d => d.Date == Today);
        Assert.Equal(Today, DayTarget.From(column)!.Date);

        var yearDay = _vm.Year.Months.SelectMany(m => m.Days).Single(d => d.Date == Today);
        Assert.Equal(Today, DayTarget.From(yearDay)!.Date);

        // 予定のある日の行。予定の無い日は畳んで1行にまとまり、その先頭の日を指す
        _test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "打ち合わせ", Date = Today });
        var row = _vm.Agenda.Rows.First(r => r.Date == Today);
        Assert.Equal(Today, DayTarget.From(row)!.Date);
        Assert.Equal(_vm.Agenda.Rows[0].Date, DayTarget.From(_vm.Agenda.Rows[0])!.Date);

        Assert.Equal(Today, DayTarget.From(Today)!.Date);
    }

    [Fact]
    public void 日付を読めない場所はメニュー情報を作らない()
    {
        Assert.Null(DayTarget.From(new object()));
        Assert.Null(DayTarget.From(null));
        Assert.Null(_vm.DayMenuFor(null));
    }

    [Fact]
    public void 時間帯の時刻は渡したぶんだけ持つ()
    {
        var cell = _vm.Month.Cells.Single(c => c.Date == Today);

        Assert.Null(DayTarget.From(cell)!.Time);
        Assert.Equal(new TimeOnly(13, 15), DayTarget.From(cell, new TimeOnly(13, 15))!.Time);
    }

    // ------------------------------------------------------------------
    // 今いるビューと同じものは出さない
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(CalendarView.Day, false, true, true)]
    [InlineData(CalendarView.Week, true, false, true)]
    [InlineData(CalendarView.Month, true, true, false)]
    [InlineData(CalendarView.Year, true, true, true)]
    [InlineData(CalendarView.Agenda, true, true, true)]
    public void 見るメニューは今いるビューと同じものを出さない(
        CalendarView view, bool showsDay, bool showsWeek, bool showsMonth)
    {
        _vm.CurrentView = view;

        var info = _vm.DayMenuFor(_vm.SelectedDay)!;

        Assert.Equal(showsDay, info.ShowsDay);
        Assert.Equal(showsWeek, info.ShowsWeek);
        Assert.Equal(showsMonth, info.ShowsMonth);
    }

    // ------------------------------------------------------------------
    // この日に予定を追加
    // ------------------------------------------------------------------

    [Fact]
    public void 時間帯の上なら_その時刻から1時間の予定で編集画面を開く()
    {
        _editors.OnEvent = _ => false;
        var target = new DayTarget(D(2026, 10, 8), new TimeOnly(13, 15));

        _vm.AddEventAtCommand.Execute(target);

        var editor = _editors.LastEventEditor!;
        Assert.Equal(D(2026, 10, 8), editor.Date);
        Assert.Equal("13:15", editor.StartTimeText);
        Assert.Equal("14:15", editor.EndTimeText);
        Assert.False(editor.IsAllDay);
        Assert.Equal(D(2026, 10, 8), _vm.SelectedDate);
    }

    [Fact]
    public void 保存すると予定が追加される()
    {
        _editors.OnEvent = editor =>
        {
            editor.Title = "打ち合わせ";
            return true;
        };

        _vm.AddEventAtCommand.Execute(new DayTarget(D(2026, 10, 8), new TimeOnly(9, 30)));

        var added = Assert.Single(_test.Workspace.Events.All());
        Assert.Equal(D(2026, 10, 8), added.Date);
        Assert.Equal(new TimeOnly(9, 30), added.StartTime);
        Assert.Equal(new TimeOnly(10, 30), added.EndTime);
    }

    [Fact]
    public void 時間帯でなければ_ふつうの予定の追加と同じ_終日にはしない()
    {
        _editors.OnEvent = _ => false;

        _vm.AddEventAtCommand.Execute(new DayTarget(D(2026, 10, 8), null));

        var editor = _editors.LastEventEditor!;
        Assert.Equal(D(2026, 10, 8), editor.Date);
        Assert.False(editor.IsAllDay);

        // 既存の「この日に予定を追加」（年ビューなど）と同じ編集画面の既定
        var viaExisting = _editors.LastEventEditor;
        _vm.AddEventOnCommand.Execute(D(2026, 10, 8));
        Assert.Equal(viaExisting!.StartTimeText, _editors.LastEventEditor!.StartTimeText);
        Assert.Equal(viaExisting.EndTimeText, _editors.LastEventEditor.EndTimeText);
        Assert.Equal(D(2026, 10, 8), _vm.SelectedDate);
    }

    // ------------------------------------------------------------------
    // この日にタスクを追加
    // ------------------------------------------------------------------

    [Fact]
    public void この日にタスクを追加は期限がその日の編集画面を開く()
    {
        _editors.OnTask = _ => false;

        _vm.AddTaskOnCommand.Execute(D(2026, 10, 8));

        var editor = _editors.LastTaskEditor!;
        Assert.True(editor.HasDue);
        Assert.Equal(D(2026, 10, 8), editor.Due);
        Assert.Equal(D(2026, 10, 8), _vm.SelectedDate);
    }

    [Fact]
    public void タスクを保存すると期限がその日のタスクが追加される()
    {
        _editors.OnTask = editor =>
        {
            editor.Title = "見積もり";
            return true;
        };

        _vm.AddTaskOnCommand.Execute(D(2026, 10, 8));

        var added = Assert.Single(_test.Workspace.Tasks.All());
        Assert.Equal("見積もり", added.Title);
        Assert.Equal(D(2026, 10, 8), added.Due);
    }

    // ------------------------------------------------------------------
    // 日・週・月を見る
    // ------------------------------------------------------------------

    [Fact]
    public void この日を1日で見るは日ビューへ移りその日を選ぶ()
    {
        _vm.ShowDayOfCommand.Execute(D(2026, 10, 14));

        Assert.Equal(CalendarView.Day, _vm.CurrentView);
        Assert.Equal(D(2026, 10, 14), _vm.SelectedDate);
        Assert.Equal(D(2026, 10, 14), _vm.Day.Date);
    }

    [Fact]
    public void この週を見るは週ビューへ移り_その日を含む週を出す()
    {
        _vm.ShowWeekOfCommand.Execute(D(2026, 10, 14));

        Assert.Equal(CalendarView.Week, _vm.CurrentView);
        Assert.Equal(D(2026, 10, 14), _vm.SelectedDate);
        Assert.Contains(_vm.Week.Days, d => d.Date == D(2026, 10, 14));
    }

    [Fact]
    public void この月を見るは月ビューへ移り_その日の月を出す()
    {
        _vm.CurrentView = CalendarView.Year;

        _vm.ShowMonthOfCommand.Execute(D(2026, 12, 3));

        Assert.Equal(CalendarView.Month, _vm.CurrentView);
        Assert.Equal(D(2026, 12, 3), _vm.SelectedDate);
        Assert.Equal(12, _vm.Month.Month.Month);
    }

    [Fact]
    public void すでにそのビューにいても_その日へ移る()
    {
        _vm.CurrentView = CalendarView.Week;

        _vm.ShowWeekOfCommand.Execute(D(2026, 10, 28));

        Assert.Equal(D(2026, 10, 28), _vm.SelectedDate);
        Assert.Contains(_vm.Week.Days, d => d.Date == D(2026, 10, 28));
    }

    [Fact]
    public void 日付を渡されなければ何もしない()
    {
        var before = _vm.CurrentView;

        _vm.ShowWeekOfCommand.Execute(null);
        _vm.AddTaskOnCommand.Execute(null);

        Assert.Equal(before, _vm.CurrentView);
        Assert.Null(_editors.LastTaskEditor);
    }
}
