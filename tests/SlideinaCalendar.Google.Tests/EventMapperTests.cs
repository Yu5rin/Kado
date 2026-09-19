using System.Text.Json;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// Google Calendar のイベントの読み書き。
/// <para>
/// 終日予定の終了日がいちばんの落とし穴。Google は<b>翌日</b>を指す（排他）ので、
/// 取り違えると同期のたびに予定が1日ずつ伸びる。
/// </para>
/// </summary>
public class EventMapperTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void 時刻つきの予定を読める()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "abc123",
              "summary": "10月度 生産台数計画 レビュー",
              "description": "確定値の擦り合わせ",
              "location": "第2会議室",
              "status": "confirmed",
              "updated": "2026-09-19T01:23:45.000Z",
              "start": { "dateTime": "2026-09-24T09:00:00+09:00" },
              "end":   { "dateTime": "2026-09-24T10:30:00+09:00" }
            }
            """), "primary");

        Assert.Equal("10月度 生産台数計画 レビュー", value.Title);
        Assert.Equal(D(2026, 9, 24), value.Date);
        Assert.Equal(new TimeOnly(9, 0), value.StartTime);
        Assert.Equal(new TimeOnly(10, 30), value.EndTime);
        Assert.Equal("第2会議室", value.Location);
        Assert.Equal("確定値の擦り合わせ", value.Note);
        Assert.Equal("abc123", value.GoogleEventId);
        Assert.Equal("primary", value.CalendarId);

        // 単日なので終了日は持たない
        Assert.Null(value.EndDate);
    }

    [Fact]
    public void 一日だけの終日予定で日付が増えない()
    {
        // Google は end.date に翌日を入れる
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e1", "summary": "棚卸し",
              "start": { "date": "2026-09-24" },
              "end":   { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.True(value.IsAllDay);
        Assert.Equal(D(2026, 9, 24), value.Date);

        // 24日だけの予定。25日までではない
        Assert.Null(value.EndDate);
        Assert.Equal(D(2026, 9, 24), value.LastDate);
    }

    [Fact]
    public void 複数日の終日予定は最終日を含む形にする()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e2", "summary": "出張",
              "start": { "date": "2026-09-24" },
              "end":   { "date": "2026-09-27" }
            }
            """), "primary");

        // 24・25・26 の3日間。27日は含まない
        Assert.Equal(D(2026, 9, 24), value.Date);
        Assert.Equal(D(2026, 9, 26), value.EndDate);
    }

    [Fact]
    public void 終日予定は書き戻すとき翌日を送る()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24),
        });

        Assert.Equal("2026-09-24", body["start"]!["date"]!.GetValue<string>());

        // 排他なので翌日
        Assert.Equal("2026-09-25", body["end"]!["date"]!.GetValue<string>());
    }

    [Fact]
    public void 終日予定は読んで書いても日付が動かない()
    {
        // 同期のたびに1日ずつ伸びる不具合を防ぐ。往復して元に戻ることを見る
        const string original = """
            {
              "id": "e3", "summary": "連休の作業",
              "start": { "date": "2026-09-21" },
              "end":   { "date": "2026-09-24" }
            }
            """;

        var read = EventMapper.FromGoogle(Json(original), "primary");
        var written = EventMapper.ToGoogle(read);

        Assert.Equal("2026-09-21", written["start"]!["date"]!.GetValue<string>());
        Assert.Equal("2026-09-24", written["end"]!["date"]!.GetValue<string>());

        // もう一往復しても動かない
        var again = EventMapper.FromGoogle(
            Json($$"""
                {"id":"e3","summary":"連休の作業","start":{{written["start"]!.ToJsonString()}},
                 "end":{{written["end"]!.ToJsonString()}}}
                """), "primary");

        Assert.Equal(read.Date, again.Date);
        Assert.Equal(read.EndDate, again.EndDate);
    }

    [Fact]
    public void 時刻つきで日をまたぐ予定は終了日を持つ()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e4", "summary": "夜間作業",
              "start": { "dateTime": "2026-09-24T22:00:00+09:00" },
              "end":   { "dateTime": "2026-09-25T06:00:00+09:00" }
            }
            """), "primary");

        Assert.Equal(D(2026, 9, 24), value.Date);

        // 終了日を持たせないと1日目だけの予定に見える
        Assert.Equal(D(2026, 9, 25), value.EndDate);
        Assert.Equal(new TimeOnly(6, 0), value.EndTime);
    }

    [Fact]
    public void URLはsourceから読む()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e5", "summary": "図面レビュー",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "source": { "url": "https://example.com/zumen", "title": "図面一覧" }
            }
            """), "primary");

        Assert.Equal("https://example.com/zumen", value.Url);
        Assert.Equal("図面一覧", value.SourceTitle);
    }

    [Fact]
    public void URLが無ければsourceを送らない()
    {
        // 片方だけの source は Google に弾かれる
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "打ち合わせ", Date = D(2026, 9, 24), SourceTitle = "見出しだけ",
        });

        Assert.Null(body["source"]);
    }

    [Fact]
    public void 書き戻す本文には扱わない項目を入れない()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        // 配列を送ると丸ごと置き換わる。空で送るとゲストが全員消える
        Assert.False(body.ContainsKey("attendees"));
        Assert.False(body.ContainsKey("reminders"));
        Assert.False(body.ContainsKey("conferenceData"));
        Assert.False(body.ContainsKey("colorId"));
        Assert.False(body.ContainsKey("visibility"));
    }

    [Fact]
    public void 取り消しを見分けられる()
    {
        Assert.True(EventMapper.IsCancelled(Json("""{"id":"e1","status":"cancelled"}""")));
        Assert.False(EventMapper.IsCancelled(Json("""{"id":"e1","status":"confirmed"}""")));

        // 状態が無いイベントは取り消しではない
        Assert.False(EventMapper.IsCancelled(Json("""{"id":"e1"}""")));
    }

    [Fact]
    public void 受け取ったままなら書き戻さない()
    {
        const string source = """
            {
              "id": "e6", "summary": "定例", "description": null, "location": null,
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": []
            }
            """;

        var value = EventMapper.FromGoogle(Json(source), "primary");

        // 毎回送ると更新時刻が動き、相手側でも「変わった」と見えてしまう
        Assert.False(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void 変えたら書き戻す()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e7", "summary": "定例",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.True(EventMapper.NeedsPush(value with { Title = "定例（変更）" }));
        Assert.True(EventMapper.NeedsPush(value with { Location = "第2会議室" }));
        Assert.True(EventMapper.NeedsPush(value with { Date = D(2026, 9, 25) }));
    }

    [Fact]
    public void 一度も受け取っていなければ書き戻す()
    {
        // 比べる相手が無い
        Assert.True(EventMapper.NeedsPush(new CalendarEvent
        {
            Id = "local1", Title = "手で入れた予定", Date = D(2026, 9, 24),
        }));
    }

    [Fact]
    public void ローカルの識別子は引き継ぐ()
    {
        const string source = """
            {
              "id": "e8", "summary": "定例",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """;

        var existing = new CalendarEvent { Id = "元からの ID", Title = "古い名前" };
        var value = EventMapper.FromGoogle(Json(source), "primary", existing);

        // 変えると作業時間や Undo の参照が切れる
        Assert.Equal("元からの ID", value.Id);
        Assert.Equal("定例", value.Title);
    }

    [Fact]
    public void 繰り返しを読み書きできる()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e9", "summary": "週次レビュー",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH"]
            }
            """), "primary");

        Assert.Equal("FREQ=WEEKLY;BYDAY=TH", value.Recurrence);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=TH", EventMapper.ToGoogle(value)["recurrence"]![0]!.GetValue<string>());
    }

    [Fact]
    public void 繰り返しを外すときは空の配列を送る()
    {
        // null だと「触らない」になってしまう
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "単発になった予定", Date = D(2026, 9, 24), Recurrence = null,
        });

        Assert.NotNull(body["recurrence"]);
        Assert.Empty(body["recurrence"]!.AsArray());
    }
}
