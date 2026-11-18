using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Presentation.Links;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 予定の題名の横に出す印（地球儀＝リンク、クリップ＝添付）を出すかどうかの判断。
/// 出す条件は右クリックの「リンクを開く」「添付を開く」と同じ（<see cref="OpenTargets"/> の読み方を共有する）。
/// </summary>
public class EventMarksTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static CalendarEvent Plain() => new() { Id = "e1", Title = "定例", Date = D(2026, 9, 24) };

    private static string GoogleRaw(params string[] urls)
    {
        var array = new JsonArray();
        foreach (var url in urls)
        {
            array.Add(new JsonObject { ["fileId"] = "id", ["fileUrl"] = url, ["title"] = "資料" });
        }

        return new JsonObject { ["attachments"] = array }.ToJsonString();
    }

    private static EventChipViewModel Chip(CalendarEvent value, bool showsTitle = true) =>
        new(new ScheduledEvent(value, value.Date), null, showsTitle);

    // ------------------------------------------------------------------
    // 判断（OpenTargets.MarksOf）
    // ------------------------------------------------------------------

    [Fact]
    public void 何も持たない予定には印を出さない()
    {
        var marks = OpenTargets.MarksOf(Plain());

        Assert.False(marks.HasLink);
        Assert.False(marks.HasAttachments);
        Assert.False(marks.Any);
        Assert.Equal(EventMarkSet.None, OpenTargets.MarksOf(null));
    }

    [Theory]
    [InlineData("https://example.com/spec", true)]
    [InlineData("http://example.com/spec", true)]
    [InlineData("ftp://example.com/spec", false)]
    [InlineData("file:///C:/a.exe", false)]
    [InlineData("www.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 地球儀は右クリックのリンクを開くと同じ条件で出す(string? url, bool shown)
    {
        var value = Plain() with { Url = url };

        Assert.Equal(shown, OpenTargets.MarksOf(value).HasLink);
        Assert.Equal(OpenTargets.For(value).HasLink, OpenTargets.MarksOf(value).HasLink);
    }

    [Fact]
    public void クリップはGoogleから受け取った添付があれば出す()
    {
        var value = Plain() with { GoogleRaw = GoogleRaw("https://drive.google.com/file/d/1/view") };

        Assert.True(OpenTargets.MarksOf(value).HasAttachments);
        Assert.False(OpenTargets.MarksOf(value).HasLink);
    }

    [Fact]
    public void クリップは送る前の添付の指定でも出す()
    {
        var value = Plain() with
        {
            PendingAttachments = EventMapper.ToPendingAttachmentsJson(
                [new EventAttachment("2", "https://drive.google.com/file/d/2/view", "足した.pdf", null)]),
        };

        Assert.True(OpenTargets.MarksOf(value).HasAttachments);
    }

    [Fact]
    public void 送る前の指定で添付を全部外した予定にはクリップを出さない()
    {
        // 編集画面で外した直後。GoogleRaw に古い添付が残っていても、メニューは出さない
        var value = Plain() with
        {
            GoogleRaw = GoogleRaw("https://drive.google.com/file/d/1/view"),
            PendingAttachments = EventMapper.ToPendingAttachmentsJson([]),
        };

        Assert.False(OpenTargets.MarksOf(value).HasAttachments);
        Assert.Equal(OpenTargets.For(value).HasAttachments, OpenTargets.MarksOf(value).HasAttachments);
    }

    [Fact]
    public void 開けない添付だけの予定にはクリップを出さない()
    {
        var value = Plain() with { GoogleRaw = GoogleRaw("http://example.com/a.pdf", "file:///C:/a.exe") };

        Assert.False(OpenTargets.MarksOf(value).HasAttachments);
    }

    [Fact]
    public void 壊れた生データの予定には印を出さない()
    {
        var value = Plain() with { GoogleRaw = "{これはJSONではない" };

        Assert.False(OpenTargets.MarksOf(value).Any);
    }

    [Fact]
    public void リンクと添付の両方を持てば両方出す()
    {
        var value = Plain() with
        {
            Url = "https://example.com/spec",
            GoogleRaw = GoogleRaw("https://drive.google.com/file/d/1/view"),
        };

        var marks = OpenTargets.MarksOf(value);

        Assert.True(marks.HasLink);
        Assert.True(marks.HasAttachments);
    }

    // ------------------------------------------------------------------
    // 各画面の ViewModel
    // ------------------------------------------------------------------

    private static CalendarEvent Both() => Plain() with
    {
        Url = "https://example.com/spec",
        GoogleRaw = GoogleRaw("https://drive.google.com/file/d/1/view"),
    };

    [Fact]
    public void 月のチップと終日の予定は印の有無を持つ()
    {
        var chip = Chip(Both());
        var none = Chip(Plain());

        Assert.True(chip.HasLink);
        Assert.True(chip.HasAttachments);
        Assert.False(none.HasLink);
        Assert.False(none.HasAttachments);
    }

    [Fact]
    public void 複数日の途中の日のように題名を出さないチップには印も出さない()
    {
        var value = Both() with { EndDate = D(2026, 9, 27) };
        var middle = new EventChipViewModel(new ScheduledEvent(value, D(2026, 9, 25), IsContinuation: true), null, false);
        var start = new EventChipViewModel(new ScheduledEvent(value, value.Date), null, false);

        Assert.Equal(ChipSpan.Middle, middle.Span);
        Assert.False(middle.HasLink);
        Assert.False(middle.HasAttachments);

        // 初日は頼まれなくても題名を出すので、印も出る
        Assert.True(start.HasLink);
        Assert.True(start.HasAttachments);
    }

    [Fact]
    public void 右パネルと一覧の行は印の有無を持つ()
    {
        var row = new DayEventViewModel(new ScheduledEvent(Both(), D(2026, 9, 24)));
        var none = new DayEventViewModel(new ScheduledEvent(Plain(), D(2026, 9, 24)));

        Assert.True(row.HasLink);
        Assert.True(row.HasAttachments);
        Assert.False(none.HasLink);
        Assert.False(none.HasAttachments);
    }

    [Fact]
    public void 時間軸のブロックは印の有無を持つ()
    {
        var block = new TimeBlockViewModel(
            "e1", "定例", new TimeOnly(9, 0), new TimeOnly(10, 0), 0, 40, null, null, Both(), D(2026, 9, 24));
        var none = new TimeBlockViewModel(
            "e2", "定例", new TimeOnly(9, 0), new TimeOnly(10, 0), 0, 40, null, null, Plain(), D(2026, 9, 24));
        var detached = new TimeBlockViewModel(
            "e3", "定例", new TimeOnly(9, 0), new TimeOnly(10, 0), 0, 40, null, null);

        Assert.True(block.HasLink);
        Assert.True(block.HasAttachments);
        Assert.False(none.HasLink);
        Assert.False(none.HasAttachments);
        Assert.False(detached.HasLink);
        Assert.False(detached.HasAttachments);
    }
}
