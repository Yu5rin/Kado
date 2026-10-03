using Kado.Data.Models;

namespace Kado.Presentation.Tests;

/// <summary>
/// 重複の整理が、ゲスト・会議 URL・添付を持つ予定や、他人が主催する予定を消さないこと。
/// <para>
/// これらは Google 側にだけある情報で、手元の行を消して Google からも消すと、取り戻せない。
/// 場所やメモが空なだけの「軽い」ほうを残すと、まさにそれが消える。
/// </para>
/// </summary>
public class DuplicateGuestTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static CalendarEvent Event(string id, string? raw = null, string? googleId = null, string? location = null) =>
        new()
        {
            Id = id, Title = "課内会議", Date = Today,
            StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
            CalendarId = "local:default", GoogleEventId = googleId, Location = location,
            GoogleRaw = raw is null ? null : "{\"id\":\"" + googleId + "\"," + raw.Replace('\'', '"') + "}",
        };

    /// <summary>場所もメモも持つ、一見「濃い」重複の相手。</summary>
    private static CalendarEvent Plain(string id) =>
        Event(id, raw: "'summary':'課内会議'", googleId: "gp", location: "第2会議室") with { Note = "議事録あり" };

    public static TheoryData<string> GoogleOnlyData => new()
    {
        "'attendees':[{'email':'a@example.com'}]",
        "'conferenceData':{'conferenceId':'abc-defg-hij'}",
        "'hangoutLink':'https://meet.google.com/abc-defg-hij'",
        "'attachments':[{'fileUrl':'https://example.com/a.pdf','title':'資料'}]",
        "'organizer':{'self':false,'email':'boss@example.com'}",
    };

    [Theory]
    [MemberData(nameof(GoogleOnlyData))]
    public void ゲストや会議URLなどを持つほうを必ず残す(string raw)
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Plain("plain"));
        test.Workspace.AddEvent(Event("guest", raw, googleId: "gg"));

        var extra = test.Workspace.FindDuplicateEvents();

        Assert.Equal("plain", extra.Single().Id);
    }

    [Fact]
    public void 整理を実行してもゲストを持つほうは残る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Plain("plain"));
        test.Workspace.AddEvent(Event("guest", "'attendees':[{'email':'a@example.com'}]", googleId: "gg"));

        Assert.Equal(1, test.Workspace.RemoveDuplicateEvents());

        Assert.Equal("guest", test.Workspace.Events.All().Single(e => e.Title == "課内会議").Id);
    }

    [Fact]
    public void どちらもゲストを持つときはどちらも消さない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Event("g1", "'attendees':[{'email':'a@example.com'}]", googleId: "gg1"));
        test.Workspace.AddEvent(Event("g2", "'attendees':[{'email':'b@example.com'}]", googleId: "gg2"));

        Assert.Empty(test.Workspace.FindDuplicateEvents());
    }

    [Fact]
    public void ゲストを持たない重複は今までどおり中身の濃いほうを残す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Plain("rich"));
        test.Workspace.AddEvent(Event("bare"));

        Assert.Equal("bare", test.Workspace.FindDuplicateEvents().Single().Id);
    }

    [Fact]
    public void 空の配列や自分が主催の予定はゲストと見ない()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Plain("rich"));
        test.Workspace.AddEvent(Event("mine", "'attendees':[],'organizer':{'self':true}", googleId: "gm"));

        // 「軽いほう」が消せる（保護の対象ではない）
        Assert.Equal("mine", test.Workspace.FindDuplicateEvents().Single().Id);
    }
}
