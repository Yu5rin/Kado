using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Presentation.Links;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 右クリックメニューの「リンクを開く」「添付を開く」を出すかどうか（開ける先があるか）の読み出し。
/// 項目を出す・隠すの判断はここで決まる（XAML は <c>HasLink</c> / <c>HasAttachments</c> を見るだけ）。
/// </summary>
public class OpenTargetsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>Google から受け取った姿（添付つき）の予定。</summary>
    private static CalendarEvent EventWithGoogleAttachments(params (string Url, string? Title)[] attachments)
    {
        var array = new JsonArray();
        foreach (var (url, title) in attachments)
        {
            array.Add(new JsonObject { ["fileId"] = "id", ["fileUrl"] = url, ["title"] = title });
        }

        return new CalendarEvent
        {
            Id = "e1", Title = "レビュー", Date = D(2026, 9, 24),
            GoogleRaw = new JsonObject { ["attachments"] = array }.ToJsonString(),
        };
    }

    // ------------------------------------------------------------------
    // 予定
    // ------------------------------------------------------------------

    [Fact]
    public void 何も持たない予定は何も出さない()
    {
        var targets = OpenTargets.For(new CalendarEvent { Id = "e1", Title = "定例" });

        Assert.False(targets.HasLink);
        Assert.False(targets.HasAttachments);
        Assert.False(targets.HasAny);
    }

    [Fact]
    public void URLがhttpsの予定はリンクを出す()
    {
        var targets = OpenTargets.For(new CalendarEvent { Id = "e1", Title = "定例", Url = "https://example.com/spec" });

        Assert.True(targets.HasLink);
        Assert.Equal("https://example.com/spec", targets.LinkUrl);
        Assert.False(targets.HasAttachments);
    }

    [Theory]
    [InlineData("http://example.com/spec", true)]
    [InlineData("ftp://example.com/spec", false)]
    [InlineData("file:///C:/a.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("www.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void リンクはhttpかhttpsのときだけ出す(string? url, bool shown)
    {
        var targets = OpenTargets.For(new CalendarEvent { Id = "e1", Title = "定例", Url = url });

        Assert.Equal(shown, targets.HasLink);
    }

    [Fact]
    public void 予定の添付はGoogleから受け取った姿から読む()
    {
        var targets = OpenTargets.For(EventWithGoogleAttachments(
            ("https://drive.google.com/file/d/1/view", "資料.pdf"),
            ("https://drive.google.com/file/d/2/view", "図面.dwg")));

        Assert.True(targets.HasAttachments);
        Assert.Equal(["資料.pdf", "図面.dwg"], targets.Attachments.Select(a => a.Label));
        Assert.All(targets.Attachments, a => Assert.IsType<EventAttachment>(a.Target));
    }

    [Fact]
    public void 送る前の添付の指定があればそれを読む()
    {
        // 編集画面で足した直後（まだ Google に送っていない）も、開ける
        var value = EventWithGoogleAttachments(("https://drive.google.com/file/d/1/view", "古い.pdf")) with
        {
            PendingAttachments = EventMapper.ToPendingAttachmentsJson(
                [new EventAttachment("2", "https://drive.google.com/file/d/2/view", "足した.pdf", null)]),
        };

        var targets = OpenTargets.For(value);

        Assert.Equal(["足した.pdf"], targets.Attachments.Select(a => a.Label));
    }

    [Fact]
    public void 予定の添付はhttpsのものだけ並べる()
    {
        var targets = OpenTargets.For(EventWithGoogleAttachments(
            ("https://drive.google.com/file/d/1/view", "安全.pdf"),
            ("http://example.com/a.pdf", "暗号化されない.pdf"),
            ("file:///C:/a.exe", "ローカル.exe")));

        // 開けないものを並べて押させるより、並べない
        Assert.Equal(["安全.pdf"], targets.Attachments.Select(a => a.Label));
    }

    [Fact]
    public void 開ける添付が1つも無ければ項目ごと隠す()
    {
        var targets = OpenTargets.For(EventWithGoogleAttachments(("http://example.com/a.pdf", "暗号化されない.pdf")));

        Assert.False(targets.HasAttachments);
        Assert.False(targets.HasAny);
    }

    [Fact]
    public void 題の無い添付はURLの最後の部分を名前にする()
    {
        var targets = OpenTargets.For(EventWithGoogleAttachments(("https://example.com/files/spec.pdf", null)));

        Assert.Equal("spec.pdf", Assert.Single(targets.Attachments).Label);
    }

    [Fact]
    public void 壊れた生データの予定は添付なしとして扱う()
    {
        var targets = OpenTargets.For(new CalendarEvent { Id = "e1", Title = "定例", GoogleRaw = "{これはJSONではない" });

        Assert.False(targets.HasAny);
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    [Fact]
    public void 何も持たないタスクは何も出さない()
    {
        Assert.False(OpenTargets.For(new TaskItem { Id = "t1", Title = "集計" }).HasAny);
    }

    [Fact]
    public void タスクのURLとファイルの場所を読む()
    {
        var targets = OpenTargets.For(new TaskItem
        {
            Id = "t1", Title = "集計", Url = "https://example.com/spec",
            Attachments = TaskAttachments.ToJson([new TaskAttachment(@"C:\資料\図面.pdf"), new TaskAttachment(@"\\server\share\議事録")]),
        });

        Assert.Equal("https://example.com/spec", targets.LinkUrl);
        Assert.Equal(["図面.pdf", "議事録"], targets.Attachments.Select(a => a.Label));
        Assert.All(targets.Attachments, a => Assert.IsType<TaskAttachment>(a.Target));
    }

    [Fact]
    public void タスクのURLがhttp以外ならリンクを出さない()
    {
        var targets = OpenTargets.For(new TaskItem { Id = "t1", Title = "集計", Url = "file:///C:/Windows/System32/calc.exe" });

        Assert.False(targets.HasLink);
    }

    // ------------------------------------------------------------------
    // 右クリックされた行の種類ごと
    // ------------------------------------------------------------------

    [Fact]
    public void 画面ごとの行の種類のすべてから読める()
    {
        using var test = TestWorkspace.Create();
        var eventWithLink = new CalendarEvent
        {
            Id = "e1", Title = "レビュー", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
            Url = "https://example.com/event",
        };
        var allDayWithLink = eventWithLink with { Id = "e2", StartTime = null, EndTime = null };
        var task = new TaskItem { Id = "t1", Title = "集計", Due = D(2026, 9, 24), Url = "https://example.com/task" };
        test.Workspace.AddEvent(eventWithLink);
        test.Workspace.AddEvent(allDayWithLink);
        test.Workspace.AddTask(task);

        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24));
        vm.SwitchViewCommand.Execute(CalendarView.Week);
        var column = vm.Week.Days.Single(d => d.Date == D(2026, 9, 24));
        var cell = vm.Month.Cells.Single(c => c.Date == D(2026, 9, 24));

        // 月：予定のチップ・タスクのチップ
        Assert.Equal("https://example.com/event", OpenTargets.From(Assert.Single(cell.Events, e => e.Id == "e1")).LinkUrl);
        Assert.Equal("https://example.com/task", OpenTargets.From(Assert.Single(cell.Tasks)).LinkUrl);

        // 週・日：時間軸のブロック・終日のチップ・終日レーンのタスク
        Assert.Equal("https://example.com/event", OpenTargets.From(Assert.Single(column.Blocks)).LinkUrl);
        Assert.Equal("https://example.com/event", OpenTargets.From(Assert.Single(column.AllDayEvents)).LinkUrl);
        Assert.Equal("https://example.com/task", OpenTargets.From(Assert.Single(column.Tasks)).LinkUrl);

        // 右ペイン・一覧：予定の行・タスクの行
        Assert.All(vm.SelectedDay.Events, e => Assert.Equal("https://example.com/event", OpenTargets.From(e).LinkUrl));
        Assert.Equal("https://example.com/task", OpenTargets.From(Assert.Single(vm.SelectedDay.Tasks)).LinkUrl);

        // 日付の行のラベル
        var milestone = new MilestoneViewModel("e1", "レビュー", eventWithLink);
        Assert.Equal("https://example.com/event", OpenTargets.From(milestone).LinkUrl);
    }

    [Fact]
    public void 知らない種類の行や空は何も出さない()
    {
        Assert.Same(OpenTargets.None, OpenTargets.From(null));
        Assert.Same(OpenTargets.None, OpenTargets.From("文字列"));
        Assert.False(OpenTargets.From(new MilestoneViewModel("m1", "仕様期限")).HasAny);
        Assert.Same(OpenTargets.None, OpenTargets.For((CalendarEvent?)null));
        Assert.Same(OpenTargets.None, OpenTargets.For((TaskItem?)null));
    }
}
