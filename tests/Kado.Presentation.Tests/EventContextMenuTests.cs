using Kado.Data.Models;
using Kado.Presentation.Menus;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 予定の右クリックメニュー（編集／詳細・複製・別のカレンダーへ移す・題名と日時をコピー・削除）。
/// <para>
/// 項目の出し分けは <see cref="MainViewModel.EventMenuFor"/>、押したときの動きは各コマンドが持つ
/// （XAML は見るだけ）。<b>Google のカレンダーを壊さない</b>決まり（送れないものは編集させない・
/// 移すときは移す経路・複製は新規として作る）が守られているかを見る。
/// </para>
/// </summary>
public class EventContextMenuTests : IDisposable
{
    // 2026/10/6 は火曜日
    private static readonly DateOnly Today = new(2026, 10, 6);

    private readonly TestWorkspace _test = TestWorkspace.Create();
    private readonly FakeEditorPresenter _editors = new();
    private readonly FakeClipboard _clipboard = new();
    private MainViewModel? _vm;

    public void Dispose() => _test.Dispose();

    /// <summary>登録を済ませてから作る（左パネルの一覧は作ったときに読む）。</summary>
    private MainViewModel Vm => _vm ??= new MainViewModel(
        _test.Workspace, Today, editors: _editors, clipboard: _clipboard);

    private static CalendarSource Google(string id, string name, int order, string role = "owner") => new()
    {
        Id = id, Summary = name, SortOrder = order, BackgroundColor = "#336699",
        GoogleRaw = $$"""{"id":"{{id}}","accessRole":"{{role}}"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    private CalendarSource LocalCalendar(string name) => _test.Workspace.CreateCalendar(name);

    private static CalendarEvent Event(string id, string calendarId, string? googleId = null) => new()
    {
        Id = id, Title = "打ち合わせ", Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        Location = "第2会議室", Note = "議題は別紙", Url = "https://example.com/spec",
        Color = "#ff8800", Notify = true,
        CalendarId = calendarId, GoogleEventId = googleId,
        GoogleCalendarId = googleId is null ? null : calendarId,
        GoogleRaw = googleId is null ? null : $$"""{"id":"{{googleId}}","summary":"打ち合わせ"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    private string DefaultCalendarId => Vm.SourceLists.DefaultCalendar!.Id;

    // ------------------------------------------------------------------
    // 複製
    // ------------------------------------------------------------------

    [Fact]
    public void 複製は同じ日同じ時刻同じカレンダーに題名場所メモURL色通知を写した予定を1件作る()
    {
        var home = DefaultCalendarId;
        _test.Workspace.AddEvent(Event("e1", home));

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");
        Assert.Equal("打ち合わせ", copy.Title);
        Assert.Equal(Today, copy.Date);
        Assert.Null(copy.EndDate);
        Assert.Equal(new TimeOnly(10, 0), copy.StartTime);
        Assert.Equal(new TimeOnly(11, 0), copy.EndTime);
        Assert.Equal("第2会議室", copy.Location);
        Assert.Equal("議題は別紙", copy.Note);
        Assert.Equal("https://example.com/spec", copy.Url);
        Assert.Equal("#ff8800", copy.Color);
        Assert.True(copy.Notify);
        Assert.Equal(home, copy.CalendarId);
        Assert.NotEqual("e1", copy.Id);
    }

    [Fact]
    public void 複製は編集画面を開かず_複製しましたと伝える()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.Null(_editors.LastEventEditor);
        Assert.Equal("複製しました", Vm.StatusMessage);
    }

    [Fact]
    public void 複製は元に戻すと消える()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));
        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));
        Assert.Equal(2, _test.Workspace.Events.All().Count);

        Assert.Equal("予定の複製", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();

        Assert.Equal(["e1"], _test.Workspace.Events.All().Select(e => e.Id));
    }

    [Fact]
    public void 複製はGoogleとの結び付きとゲストと会議URLと添付を持たない()
    {
        var calendar = Google("g-cal", "仕事", 1);
        _test.Workspace.Sources.Upsert(calendar);

        var raw = """
            {"id":"g1","summary":"打ち合わせ","attendees":[{"email":"a@example.com"}],
             "hangoutLink":"https://meet.google.com/abc","conferenceData":{"conferenceId":"abc"},
             "attachments":[{"fileId":"f1","fileUrl":"https://drive.google.com/f1","title":"資料"}]}
            """;

        _test.Workspace.AddEvent(Event("e1", "g-cal", "g1") with
        {
            GoogleRaw = raw, GoogleUpdated = "2026-10-01T00:00:00Z", Status = "confirmed",
            PendingAttachments = """[{"fileId":"f2","fileUrl":"https://drive.google.com/f2","title":"追加"}]""",
        });

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");
        Assert.Equal("g-cal", copy.CalendarId);
        Assert.Null(copy.GoogleEventId);
        Assert.Null(copy.GoogleCalendarId);
        Assert.Null(copy.GoogleRaw);
        Assert.Null(copy.GoogleUpdated);
        Assert.Null(copy.PendingAttachments);
        Assert.False(copy.GoogleMissing);

        // 元の予定は触らない
        var original = _test.Workspace.Events.Find("e1")!;
        Assert.Equal("g1", original.GoogleEventId);
        Assert.Equal(raw, original.GoogleRaw);
    }

    [Fact]
    public void 複製は新規として作られるので_元に戻すとGoogleに送った分は消す記録を残す()
    {
        // 同期が結び付けたあとに Ctrl＋Z で消したときは、Google からも消える（AddEventEdit の決まり）
        var calendar = Google("g-cal", "仕事", 1);
        _test.Workspace.Sources.Upsert(calendar);
        _test.Workspace.AddEvent(Event("e1", "g-cal", "g1"));

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));
        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");

        // 同期が結び付けた
        _test.Workspace.Events.Upsert(copy with { GoogleEventId = "g2", GoogleCalendarId = "g-cal" });

        _test.Workspace.UndoLast();

        Assert.Null(_test.Workspace.Events.Find(copy.Id));
        Assert.Equal(1, _test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 繰り返しの予定の1回分を複製すると_その日だけの単発の予定になる()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        Vm.SelectedDate = Today.AddDays(7);

        // 右ペインに出ている 10/13 の回
        var row = Assert.Single(Vm.SelectedDay.Events);
        Vm.DuplicateEventCommand.Execute(row);

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");
        Assert.Equal(Today.AddDays(7), copy.Date);
        Assert.Null(copy.Recurrence);
        Assert.Equal(new TimeOnly(10, 0), copy.StartTime);

        // 元の繰り返しは変えない
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", _test.Workspace.Events.Find("e1")!.Recurrence);
    }

    [Fact]
    public void 複数日の予定を複製すると期間ごと写す()
    {
        _test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "trip", Title = "出張", Date = Today, EndDate = Today.AddDays(2), CalendarId = DefaultCalendarId,
        });

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("trip"));

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "trip");
        Assert.Equal(Today, copy.Date);
        Assert.Equal(Today.AddDays(2), copy.EndDate);
        Assert.Null(copy.StartTime);
    }

    [Fact]
    public void 読み取り専用のカレンダーの予定を複製すると_書き込める既定のカレンダーに入る()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有カレンダー", -10, role: "reader"));
        _test.Workspace.AddEvent(Event("e1", "g-ro", "g1"));

        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));
        Assert.True(info.CanDuplicate);

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");
        Assert.Equal(DefaultCalendarId, copy.CalendarId);
        Assert.NotEqual("g-ro", copy.CalendarId);
        Assert.Equal("複製しました", Vm.StatusMessage);
    }

    [Fact]
    public void 実働日のカレンダーの予定を複製しても_取り込みで消える入れ先には入れない()
    {
        var ina = _test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        _test.Workspace.AddEvent(Event("e1", ina.Id));

        Vm.DuplicateEventCommand.Execute(_test.Workspace.Events.Find("e1"));

        var copy = Assert.Single(_test.Workspace.Events.All(), e => e.Id != "e1");
        Assert.NotEqual(ina.Id, copy.CalendarId);
    }

    // ------------------------------------------------------------------
    // 別のカレンダーへ移す
    // ------------------------------------------------------------------

    [Fact]
    public void 移し先は書き込めるカレンダーだけで_今のカレンダーを除き_左パネルと同じ並びと色で並ぶ()
    {
        var a = LocalCalendar("A");
        var b = LocalCalendar("B");
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        _test.Workspace.Sources.Upsert(Google("g-gone", "外れた", -4));
        _test.Workspace.Sources.SetCalendarDetached("g-gone", true);

        _test.Workspace.AddEvent(Event("e1", a.Id));
        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));

        Assert.True(info.CanMove);

        var names = info.MoveChoices.Select(c => c.Label).ToArray();
        Assert.DoesNotContain("A", names);
        Assert.DoesNotContain("共有", names);
        Assert.DoesNotContain("外れた", names);
        Assert.Contains("B", names);

        // 並びは左パネルの一覧と同じ
        var panel = Vm.SourceLists.Calendars.Select(c => c.Name).Where(names.Contains).ToArray();
        Assert.Equal(panel, names);

        // 色は左パネルの色見本
        var swatch = Vm.SourceLists.Calendars.Single(c => c.Id == b.Id).SwatchColor;
        Assert.Equal(swatch, info.MoveChoices.Single(c => c.Label == "B").Color);
        Assert.All(info.MoveChoices, c => Assert.True(c.IsEnabled));
    }

    [Fact]
    public void 移すと入れ先だけが変わり_Googleとの結び付きは触らない_元に戻せる()
    {
        // Google 側は events.move になる。入れ先の希望（CalendarId）だけを変え、いま実際にいる
        // カレンダー（GoogleCalendarId）は同期が確かめて動かす
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.Sources.Upsert(Google("g-b", "私用", 2));
        _test.Workspace.AddEvent(Event("e1", "g-a", "g1"));

        var choice = Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).MoveChoices.Single();
        Assert.Equal("私用", choice.Label);

        choice.Command.Execute(choice.Parameter);

        var moved = _test.Workspace.Events.Find("e1")!;
        Assert.Equal("g-b", moved.CalendarId);
        Assert.Equal("g-a", moved.GoogleCalendarId);
        Assert.Equal("g1", moved.GoogleEventId);
        Assert.Equal("「私用」へ移しました", Vm.StatusMessage);

        Assert.Equal("予定を別のカレンダーへ移す", _test.Workspace.Undo.UndoDescription);
        _test.Workspace.UndoLast();

        var restored = _test.Workspace.Events.Find("e1")!;
        Assert.Equal("g-a", restored.CalendarId);
        Assert.Equal("g-a", restored.GoogleCalendarId);
    }

    [Fact]
    public void 移すのは編集画面でカレンダーを変えて保存したときと同じ結果になる()
    {
        var a = LocalCalendar("A");
        var b = LocalCalendar("B");
        _test.Workspace.AddEvent(Event("e1", a.Id));

        // 編集画面でカレンダーを B にして保存
        _editors.OnEvent = editor =>
        {
            editor.CalendarId = b.Id;
            return true;
        };
        Vm.EditEventEntryCommand.Execute(_test.Workspace.Events.Find("e1"));
        var viaEditor = _test.Workspace.Events.Find("e1")!;

        // もう一度 A へ戻してから、メニューで B へ移す
        _test.Workspace.UpdateEvent(viaEditor with { CalendarId = a.Id });
        var choice = Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).MoveChoices.Single(c => c.Label == "B");
        choice.Command.Execute(choice.Parameter);
        var viaMenu = _test.Workspace.Events.Find("e1")!;

        Assert.Equal(viaEditor with { UpdatedAt = default }, viaMenu with { UpdatedAt = default });
    }

    [Fact]
    public void Googleと結び付いた予定にはローカルのカレンダーを移し先に出さない()
    {
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.Sources.Upsert(Google("g-b", "私用", 2));
        LocalCalendar("手元");
        _test.Workspace.AddEvent(Event("e1", "g-a", "g1"));

        var names = Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).MoveChoices.Select(c => c.Label).ToArray();

        // 移すと、Google 側の予定がこちらの管理から外れて二重に残る（編集画面の候補と同じ決まり）
        Assert.Equal(["私用"], names);
    }

    [Fact]
    public void 移し先が無ければ親の項目を灰色にして理由を出す()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));
        // 書き込めるカレンダーが1つだけ
        foreach (var other in Vm.SourceLists.Calendars.Where(c => c.Id != DefaultCalendarId).ToArray())
        {
            _test.Workspace.Sources.DropRemovedCalendar(other.Id);
        }

        _vm = null;
        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));

        Assert.False(info.CanMove);
        Assert.False(string.IsNullOrEmpty(info.MoveDisabledReason));
        Assert.Empty(info.MoveChoices);
    }

    [Fact]
    public void Googleが移させない予定は子メニューの項目を灰色にして理由を出す()
    {
        // eventType が default 以外（集中時間）は events.move が断る。編集はできるので、親は押せる
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.Sources.Upsert(Google("g-b", "私用", 2));
        _test.Workspace.AddEvent(Event("e1", "g-a", "g1") with
        {
            GoogleRaw = """{"id":"g1","summary":"打ち合わせ","eventType":"focusTime"}""",
        });

        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));

        Assert.True(info.CanEdit);
        Assert.True(info.CanMove);

        var choice = Assert.Single(info.MoveChoices);
        Assert.False(choice.IsEnabled);
        Assert.Contains("集中時間", choice.ToolTip, StringComparison.Ordinal);

        // 押されても移さない（灰色にしていない呼び方をされても、止める）
        choice.Command.Execute(choice.Parameter);
        Assert.Equal("g-a", _test.Workspace.Events.Find("e1")!.CalendarId);
        Assert.Contains("集中時間", Vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 移し先に選べないカレンダーへの依頼は受けない()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        var a = LocalCalendar("A");
        _test.Workspace.AddEvent(Event("e1", a.Id));

        Vm.MoveEventToCalendarCommand.Execute(new EventMoveRequest("e1", "g-ro"));

        Assert.Equal(a.Id, _test.Workspace.Events.Find("e1")!.CalendarId);
    }

    // ------------------------------------------------------------------
    // 編集できない予定
    // ------------------------------------------------------------------

    [Fact]
    public void 読み取り専用のカレンダーの予定は詳細を見るになり_移すは灰色で理由が出る()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        LocalCalendar("手元");
        _test.Workspace.AddEvent(Event("e1", "g-ro", "g1"));

        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));

        Assert.False(info.CanEdit);
        Assert.Equal("詳細を見る", info.EditLabel);
        Assert.False(info.CanMove);
        Assert.Contains("読み取り専用", info.MoveDisabledReason, StringComparison.Ordinal);
        Assert.Empty(info.MoveChoices);

        // Google から受け取った予定は、読み取り専用のカレンダーでは削除もできない
        Assert.False(info.CanDelete);
        Assert.Contains("読み取り専用", info.DeleteDisabledReason, StringComparison.Ordinal);

        // 複製とコピーは使える
        Assert.True(info.CanDuplicate);
    }

    [Fact]
    public void 詳細を見るは読み取り専用の編集画面を開き_保存しても何も変わらない()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        _test.Workspace.AddEvent(Event("e1", "g-ro", "g1"));
        var before = _test.Workspace.Events.Find("e1");

        // 保存を選んだことにしても、読み取り専用なので書き込まない
        _editors.OnEvent = _ => true;
        Vm.EditEventEntryCommand.Execute(_test.Workspace.Events.Find("e1"));

        var editor = _editors.LastEventEditor!;
        Assert.True(editor.IsReadOnly);
        Assert.False(editor.CanSave);
        Assert.Equal("予定の詳細", editor.HeaderText);
        Assert.Contains("読み取り専用", editor.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(editor.ToModel);

        // 削除の要求も受けない
        editor.RequestDelete();
        Assert.False(editor.Deleted);

        Assert.Equal(before, _test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void 他人が主催する予定とGoogle側で変えられない予定は詳細を見る_削除はできる()
    {
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.Sources.Upsert(Google("g-b", "私用", 2));

        string[] raws =
        [
            """{"id":"g1","organizer":{"self":false}}""",
            """{"id":"g1","locked":true}""",
            """{"id":"g1","eventType":"birthday"}""",
        ];

        foreach (var raw in raws)
        {
            _test.Workspace.AddEvent(Event("e1", "g-a", "g1") with { GoogleRaw = raw });
            var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));

            Assert.False(info.CanEdit);
            Assert.Equal("詳細を見る", info.EditLabel);
            Assert.False(info.CanMove);
            Assert.Contains("Google 側", info.MoveDisabledReason, StringComparison.Ordinal);

            // Google 側で作られた予定は、内容は変えられないが削除はできる（既存の決まり）
            Assert.True(info.CanDelete);
            Assert.True(info.CanDuplicate);

            _test.Workspace.DeleteEvent("e1");
        }
    }

    [Fact]
    public void 編集できる予定は編集画面が普通に開く()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));
        _editors.OnEvent = _ => false;

        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));
        Assert.True(info.CanEdit);
        Assert.Equal("編集", info.EditLabel);

        Vm.EditEventEntryCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.False(_editors.LastEventEditor!.IsReadOnly);
        Assert.Null(_editors.LastEventEditor.ReadOnlyReason);
    }

    [Fact]
    public void 送れないカレンダーに入ってしまった未送信の予定は手元だけで削除できる()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        _test.Workspace.AddEvent(Event("stuck", "g-ro"));

        var info = Vm.EventMenuFor(_test.Workspace.Events.Find("stuck"));
        Assert.True(info.CanDelete);

        Vm.DeleteEventEntryCommand.Execute(_test.Workspace.Events.Find("stuck"));

        Assert.Null(_test.Workspace.Events.Find("stuck"));
        Assert.Equal(0, _test.Workspace.Tombstones.Count());
    }

    [Fact]
    public void 表せない繰り返しの予定は編集のまま_日時と繰り返しだけを編集画面が止める()
    {
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.AddEvent(Event("e1", "g-a", "g1") with
        {
            GoogleRaw = """{"id":"g1","summary":"打ち合わせ","recurrence":["RRULE:FREQ=WEEKLY","RDATE:20261020T010000Z"]}""",
        });

        Assert.True(Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).CanEdit);

        _editors.OnEvent = _ => false;
        Vm.EditEventEntryCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.False(_editors.LastEventEditor!.IsReadOnly);
        Assert.False(_editors.LastEventEditor.CanChangeSchedule);
    }

    // ------------------------------------------------------------------
    // 情報の覚え（1回の右クリックで項目ごとに何度も作られるので、直近の1件を覚える）
    // ------------------------------------------------------------------

    [Fact]
    public void 変わっていなければ同じ情報を返し_データや一覧が変わったら作り直す()
    {
        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1));
        _test.Workspace.AddEvent(Event("e1", "g-a", "g1"));
        var stored = _test.Workspace.Events.Find("e1");

        var first = Vm.EventMenuFor(stored);
        Assert.Same(first, Vm.EventMenuFor(stored));
        Assert.True(first.CanEdit);

        // 同期が予定を書き換えた（向こうで変えられない予定になった）。同期のあとは、画面が読み直される
        _test.Workspace.Events.Upsert(_test.Workspace.Events.Find("e1")! with { GoogleRaw = """{"id":"g1","locked":true}""" });
        _test.Workspace.ReloadWorkingDays();
        var second = Vm.EventMenuFor(_test.Workspace.Events.Find("e1"));
        Assert.NotSame(first, second);
        Assert.False(second.CanEdit);

        // 元に戻った。カレンダーが読み取り専用に変わった（同期が一覧を書き換えて、左パネルが読み直された）
        _test.Workspace.Events.Upsert(_test.Workspace.Events.Find("e1")! with { GoogleRaw = """{"id":"g1"}""" });
        _test.Workspace.ReloadWorkingDays();
        Assert.True(Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).CanEdit);

        _test.Workspace.Sources.Upsert(Google("g-a", "仕事", 1, role: "reader"));
        Vm.SourceLists.Refresh();

        Assert.False(Vm.EventMenuFor(_test.Workspace.Events.Find("e1")).CanEdit);
    }

    // ------------------------------------------------------------------
    // 題名と日時をコピー
    // ------------------------------------------------------------------

    [Fact]
    public void 題名と日時をコピーは1行をクリップボードに置く()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));

        Vm.CopyEventTextCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.Equal("10/6(火) 10:00–11:00 打ち合わせ（第2会議室）", _clipboard.Text);
        Assert.Equal("コピーしました", Vm.StatusMessage);
    }

    [Fact]
    public void 繰り返しの予定のコピーは右クリックした回の日付を書く()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId) with { Recurrence = "FREQ=WEEKLY;BYDAY=TU" });
        Vm.SelectedDate = Today.AddDays(14);

        Vm.CopyEventTextCommand.Execute(Assert.Single(Vm.SelectedDay.Events));

        Assert.Equal("10/20(火) 10:00–11:00 打ち合わせ（第2会議室）", _clipboard.Text);
    }

    [Fact]
    public void コピーは編集できない予定にも使える()
    {
        _test.Workspace.Sources.Upsert(Google("g-ro", "共有", -5, role: "reader"));
        _test.Workspace.AddEvent(Event("e1", "g-ro", "g1"));

        Vm.CopyEventTextCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.NotNull(_clipboard.Text);
    }

    [Fact]
    public void クリップボードに置けなければ_置けなかったと伝える()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));
        _clipboard.Succeeds = false;

        Vm.CopyEventTextCommand.Execute(_test.Workspace.Events.Find("e1"));

        Assert.Null(_clipboard.Text);
        Assert.Contains("コピーできませんでした", Vm.StatusMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 右クリックされた行の型
    // ------------------------------------------------------------------

    [Fact]
    public void どの画面の行でも同じ予定として読み取れる()
    {
        _test.Workspace.AddEvent(Event("e1", DefaultCalendarId));
        _test.Workspace.AddEvent(Event("allday", DefaultCalendarId) with { StartTime = null, EndTime = null });
        _test.Workspace.AddEvent(Event("m1", DefaultCalendarId) with
        {
            Source = CalendarWorkspace.WorkingDaySource, StartTime = null, EndTime = null,
            Title = "仕様期限",
        });

        var chip = Vm.Month.Cells.Single(c => c.Date == Today).Events.First(e => e.Id == "e1");
        var row = Vm.SelectedDay.Events.First(e => e.Id == "e1");

        Vm.CurrentView = CalendarView.Week;
        var column = Vm.Week.Days.Single(d => d.Date == Today);
        var block = column.Blocks.Single();
        var allDay = column.AllDayEvents.Single(e => e.Id == "allday");
        var milestone = Assert.Single(column.Milestones);

        foreach (var entry in new object[] { chip, row, block, allDay, milestone })
        {
            Assert.NotNull(EntryRefs.EventOf(entry));
        }

        Assert.Equal("e1", EntryRefs.EventOf(chip)!.Id);
        Assert.Equal("e1", EntryRefs.EventOf(row)!.Id);
        Assert.Equal("e1", EntryRefs.EventOf(block)!.Id);
        Assert.Equal("allday", EntryRefs.EventOf(allDay)!.Id);
        Assert.Equal("m1", EntryRefs.EventOf(milestone)!.Id);

        // 回の日付も持つ（繰り返しの複製とコピーが使う）
        Assert.Equal(Today, EntryRefs.EventOf(block)!.Date);
        Assert.Equal(Today, EntryRefs.EventOf(milestone)!.Date);

        Assert.Null(EntryRefs.EventOf(new object()));
        Assert.Null(EntryRefs.EventOf(null));
    }

    [Fact]
    public void 予定が見つからなければ_何も壊さず見つかりませんでしたと伝える()
    {
        Vm.DuplicateEventCommand.Execute(new CalendarEvent { Id = "missing", Title = "x", Date = Today });
        Assert.Equal("予定が見つかりませんでした", Vm.StatusMessage);

        Vm.CopyEventTextCommand.Execute(new CalendarEvent { Id = "missing", Title = "x", Date = Today });
        Assert.Null(_clipboard.Text);

        Assert.Same(EventMenuInfo.Unavailable, Vm.EventMenuFor(new CalendarEvent { Id = "missing", Title = "x", Date = Today }));
    }
}
