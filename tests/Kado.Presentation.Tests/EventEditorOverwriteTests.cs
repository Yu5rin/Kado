using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 編集画面の保存で、Google が持つ値を落とす・書き換える筋。
/// <para>
/// source.title の引き継ぎ・Google で移せない予定のカレンダー欄・同じ名前の添付の外し方・
/// 手元で足した除外日を持つ繰り返しの扱い。
/// </para>
/// </summary>
public class EventEditorOverwriteTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly SourceChoice[] Calendars = [new("primary", "仕事"), new("other", "別")];

    /// <summary>
    /// Google から受け取った予定。<paramref name="rawBody"/> は JSON の中身で、書きやすいよう
    /// 引用符に <c>'</c> を使う。
    /// </summary>
    private static CalendarEvent FromGoogle(string rawBody, string? recurrence = null, string? sourceTitle = null) =>
        new()
        {
            Id = "e1", Title = "会議", Date = D(2026, 10, 7), CalendarId = "primary",
            GoogleEventId = "g1", GoogleCalendarId = "primary", Source = "google",
            Recurrence = recurrence, SourceTitle = sourceTitle,
            GoogleRaw = "{\"id\":\"g1\"," + rawBody.Replace('\'', '"') + "}",
        };

    // ------------------------------------------------------------------
    // source.title を落とさない
    // ------------------------------------------------------------------

    [Fact]
    public void 保存してもsourceの題を引き継ぐ()
    {
        var source = FromGoogle(
            "'source':{'url':'https://mail.example/1','title':'他の道具が付けた題'}",
            sourceTitle: "他の道具が付けた題") with { Url = "https://mail.example/1" };

        var vm = new EventEditorViewModel(source, Calendars) { Title = "会議（改）" };

        Assert.Equal("他の道具が付けた題", vm.ToModel().SourceTitle);
    }

    // ------------------------------------------------------------------
    // Google では移せない予定は、カレンダー欄を変えさせない
    // ------------------------------------------------------------------

    [Fact]
    public void 他人が主催する予定はguestsCanModifyが立っていてもカレンダーを変えさせない()
    {
        var source = FromGoogle("'organizer':{'self':false,'email':'boss@example.com'},'guestsCanModify':true");

        var vm = new EventEditorViewModel(source, Calendars);

        Assert.False(vm.CanChangeCalendar);
        Assert.Contains("主催", vm.CalendarLockReason, StringComparison.Ordinal);

        vm.CalendarId = "other";
        Assert.Equal("primary", vm.CalendarId);
    }

    [Theory]
    [InlineData("focusTime")]
    [InlineData("outOfOffice")]
    [InlineData("workingLocation")]
    [InlineData("fromGmail")]
    public void defaultでない種類の予定はカレンダーを変えさせない(string eventType)
    {
        var source = FromGoogle($"'eventType':'{eventType}'");

        var vm = new EventEditorViewModel(source, Calendars);

        Assert.False(vm.CanChangeCalendar);
        Assert.NotNull(vm.CalendarLockReason);

        vm.CalendarId = "other";
        Assert.Equal("primary", vm.CalendarId);
    }

    [Fact]
    public void 自分が主催するdefaultの予定は今までどおりカレンダーを変えられる()
    {
        var source = FromGoogle("'eventType':'default','organizer':{'self':true}");

        var vm = new EventEditorViewModel(source, Calendars);

        Assert.True(vm.CanChangeCalendar);
        Assert.Null(vm.CalendarLockReason);

        vm.CalendarId = "other";
        Assert.Equal("other", vm.CalendarId);
    }

    [Fact]
    public void 新しい予定はカレンダーを選べる()
    {
        Assert.True(new EventEditorViewModel(D(2026, 10, 7), Calendars).CanChangeCalendar);
    }

    // ------------------------------------------------------------------
    // fileId の無い添付を1件外しても、ほかの添付は外れない
    // ------------------------------------------------------------------

    [Fact]
    public void fileIdの無い添付を1件外してもほかの添付は残る()
    {
        var source = FromGoogle(
            "'attachments':[" +
            "{'fileUrl':'https://example.com/a.pdf','title':'資料A.pdf'}," +
            "{'fileUrl':'https://example.com/b.pdf','title':'資料B.pdf'}]");

        var vm = new EventEditorViewModel(source, Calendars);
        Assert.Equal(2, vm.Attachments.Count);

        vm.RemoveAttachment(vm.Attachments[0]);

        var left = Assert.Single(vm.Attachments);
        Assert.Equal("https://example.com/b.pdf", left.FileUrl);

        // 送る一覧にも残っている
        var pending = vm.ToModel().PendingAttachments;
        Assert.Contains("b.pdf", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("a.pdf", pending, StringComparison.Ordinal);
    }

    [Fact]
    public void 題が同じでもURLの違う添付は別のものとして扱う()
    {
        var source = FromGoogle(
            "'attachments':[" +
            "{'fileId':'f1','fileUrl':'https://drive.google.com/file/d/f1/view','title':'資料.pdf'}," +
            "{'fileId':'f2','fileUrl':'https://drive.google.com/file/d/f2/view','title':'資料.pdf'}]");

        var vm = new EventEditorViewModel(source, Calendars);

        vm.RemoveAttachment(vm.Attachments[1]);

        Assert.Equal("f1", Assert.Single(vm.Attachments).FileId);
    }

    // ------------------------------------------------------------------
    // 手元で足した除外日を持つ繰り返しも、ふつうの繰り返しとして扱う
    // ------------------------------------------------------------------

    [Fact]
    public void 除外日を除いたRRULEで繰り返しの種類を判定する()
    {
        Assert.Equal(RecurrenceKind.Weekly,
            RecurrenceChoice.KindOf("FREQ=WEEKLY;BYDAY=WE;EXDATE=20261014", D(2026, 10, 7)));
        Assert.Equal(RecurrenceKind.Daily,
            RecurrenceChoice.KindOf("FREQ=DAILY;EXDATE=20261014,20261015", D(2026, 10, 7)));

        // 隔週は除外日があっても独自指定のまま
        Assert.Equal(RecurrenceKind.Custom,
            RecurrenceChoice.KindOf("FREQ=WEEKLY;INTERVAL=2;BYDAY=WE;EXDATE=20261014", D(2026, 10, 7)));
    }

    [Fact]
    public void 除外日つきの毎週は曜日の追従が効き除外日も残る()
    {
        // 水曜始まりの毎週。例外回のために 10/14 を除外してある
        var source = FromGoogle(
            "'recurrence':['RRULE:FREQ=WEEKLY;BYDAY=WE']", recurrence: "FREQ=WEEKLY;BYDAY=WE;EXDATE=20261014");

        var vm = new EventEditorViewModel(source, Calendars);
        Assert.Equal(RecurrenceKind.Weekly, vm.Recurrence);

        // 触らなければ、そのまま
        Assert.Equal("FREQ=WEEKLY;BYDAY=WE;EXDATE=20261014", vm.ToModel().Recurrence);

        // 開始日を木曜に動かすと、曜日も付いてくる。除外日は残す
        vm.Date = D(2026, 10, 8);
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH;EXDATE=20261014", vm.ToModel().Recurrence);
    }

    [Fact]
    public void 繰り返さないに変えたら除外日も消える()
    {
        var source = FromGoogle(
            "'recurrence':['RRULE:FREQ=WEEKLY;BYDAY=WE']", recurrence: "FREQ=WEEKLY;BYDAY=WE;EXDATE=20261014");

        var vm = new EventEditorViewModel(source, Calendars) { Recurrence = RecurrenceKind.None };

        Assert.Null(vm.ToModel().Recurrence);
    }

    [Fact]
    public void 独自指定は除外日ごとそのまま持ち続ける()
    {
        var source = FromGoogle(
            "'recurrence':['RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=WE']",
            recurrence: "FREQ=WEEKLY;INTERVAL=2;BYDAY=WE;EXDATE=20261014");

        var vm = new EventEditorViewModel(source, Calendars);

        Assert.Equal(RecurrenceKind.Custom, vm.Recurrence);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=WE;EXDATE=20261014", vm.ToModel().Recurrence);
    }
}
