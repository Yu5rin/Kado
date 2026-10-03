using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

/// <summary>
/// 送るたびに Google 側の値を書き換えてしまう筋（時差・source・除外日）。
/// <para>
/// <b>使う人が変えていない項目は、Google が持っているままにする。</b>
/// </para>
/// </summary>
public class EventMapperOverwriteTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static string[] LinesOf(JsonObject body) =>
        body["recurrence"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    // ------------------------------------------------------------------
    // 時差（timeZone）を、この PC のものへ書き換えない
    // ------------------------------------------------------------------

    private const string NewYorkEvent = """
        {
          "id": "ny1", "summary": "米国との定例", "status": "confirmed",
          "start": { "dateTime": "2026-10-05T09:00:00-04:00", "timeZone": "America/New_York" },
          "end":   { "dateTime": "2026-10-05T10:00:00-04:00", "timeZone": "America/New_York" },
          "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO"]
        }
        """;

    [Fact]
    public void 題だけ直して送っても元のtimeZoneを保つ()
    {
        var value = EventMapper.FromGoogle(Json(NewYorkEvent), "primary");

        var body = EventMapper.ToGoogle(value with { Title = "米国との定例（改）" });

        // この PC の時差（Asia/Tokyo など）へ書き換えると、繰り返しの回の時刻が夏時間でずれる
        Assert.Equal("America/New_York", (string?)body["start"]!["timeZone"]);
        Assert.Equal("America/New_York", (string?)body["end"]!["timeZone"]);

        // 時刻も、同じ瞬間を元の時差で表したもの
        Assert.Equal("2026-10-05T09:00:00-04:00", (string?)body["start"]!["dateTime"]);
        Assert.Equal("2026-10-05T10:00:00-04:00", (string?)body["end"]!["dateTime"]);
    }

    [Fact]
    public void 日時を変えたときも元のtimeZoneで表した時刻として送る()
    {
        var value = EventMapper.FromGoogle(Json(NewYorkEvent), "primary");

        // 手元（この PC）の時刻を1時間遅らせる
        var moved = value with
        {
            StartTime = value.StartTime!.Value.AddHours(1),
            EndTime = value.EndTime!.Value.AddHours(1),
        };

        var body = EventMapper.ToGoogle(moved);

        Assert.Equal("America/New_York", (string?)body["start"]!["timeZone"]);
        Assert.Equal("2026-10-05T10:00:00-04:00", (string?)body["start"]!["dateTime"]);
        Assert.Equal("2026-10-05T11:00:00-04:00", (string?)body["end"]!["dateTime"]);
    }

    [Fact]
    public void 元にtimeZoneが無ければこのPCの地域を使う()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "t1", "summary": "定例",
              "start": { "dateTime": "2026-10-05T09:00:00Z" },
              "end":   { "dateTime": "2026-10-05T10:00:00Z" }
            }
            """), "primary");

        var body = EventMapper.ToGoogle(value);

        var local = TimeZoneInfo.Local;
        var expected = local.HasIanaId
            ? local.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;

        Assert.Equal(expected, (string?)body["start"]!["timeZone"]);
    }

    [Fact]
    public void timeZoneを保っても何も変えなければ送らない()
    {
        var value = EventMapper.FromGoogle(Json(NewYorkEvent), "primary");

        Assert.False(EventMapper.NeedsPush(value));
    }

    // ------------------------------------------------------------------
    // source.title を、題に追従させない。ほかの道具が付けた source に触らない
    // ------------------------------------------------------------------

    private const string OtherToolSource = """
        {
          "id": "s1", "summary": "メールから",
          "start": { "date": "2026-10-05" }, "end": { "date": "2026-10-06" },
          "source": { "url": "https://mail.example/1", "title": "他の道具が付けた題" }
        }
        """;

    [Fact]
    public void 題を直してもsourceは送らない()
    {
        var value = EventMapper.FromGoogle(Json(OtherToolSource), "primary");

        var body = EventMapper.ToGoogle(value with { Title = "メールから（改）" });

        // 送ると source.title が予定の題に書き換わる。URL を触っていないなら何も送らない
        Assert.False(body.ContainsKey("source"));
    }

    [Fact]
    public void URLを変えたときは新しいURLと元のsourceの題を送る()
    {
        var value = EventMapper.FromGoogle(Json(OtherToolSource), "primary");

        var body = EventMapper.ToGoogle(value with { Url = "https://mail.example/2" });

        Assert.Equal("https://mail.example/2", (string?)body["source"]!["url"]);
        Assert.Equal("他の道具が付けた題", (string?)body["source"]!["title"]);
    }

    [Fact]
    public void URLを空にしたときは消す意思として送る()
    {
        var value = EventMapper.FromGoogle(Json(OtherToolSource), "primary");

        var body = EventMapper.ToGoogle(value with { Url = null });

        Assert.True(body.ContainsKey("source"));
        Assert.Null(body["source"]);
    }

    [Fact]
    public void 新しく作る予定のsourceの題は予定の題にする()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "n1", Title = "資料", Date = new DateOnly(2026, 10, 5), Url = "https://doc.example/1",
        });

        Assert.Equal("https://doc.example/1", (string?)body["source"]!["url"]);
        Assert.Equal("資料", (string?)body["source"]!["title"]);
    }

    [Fact]
    public void 受け取ったsourceの題は読んで持ち続ける()
    {
        var value = EventMapper.FromGoogle(Json(OtherToolSource), "primary");

        Assert.Equal("他の道具が付けた題", value.SourceTitle);
        Assert.Equal("https://mail.example/1", value.Url);
    }

    // ------------------------------------------------------------------
    // 例外回のために手元で足した除外日を、親の応答の取り込みで失わない
    // ------------------------------------------------------------------

    private const string WeeklyParent = """
        {
          "id": "p1", "summary": "週次", "status": "confirmed",
          "start": { "date": "2026-10-05" }, "end": { "date": "2026-10-06" },
          "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO"]
        }
        """;

    [Fact]
    public void 親を送った応答を通しても手元で足した除外日を引き継ぐ()
    {
        var parent = EventMapper.FromGoogle(Json(WeeklyParent), "primary");

        // 例外回の取り込みが内部で足した除外日
        var withLocal = parent with
        {
            Recurrence = RecurrenceConverter.WithExceptionDate(parent.Recurrence, new DateOnly(2026, 10, 12)),
        };

        // 題を直して送った。応答の recurrence は Google の行だけ（10/12 の除外は持たない）
        var response = Json(WeeklyParent.Replace("\"週次\"", "\"週次（改）\""));
        var after = EventMapper.FromGoogle(response, "primary", withLocal);

        Assert.Equal("週次（改）", after.Title);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;EXDATE=20261012", after.Recurrence);
    }

    [Fact]
    public void Googleにもある除外日は二重にならない()
    {
        var parent = EventMapper.FromGoogle(Json(WeeklyParent), "primary");
        var withLocal = parent with
        {
            Recurrence = RecurrenceConverter.WithExceptionDate(parent.Recurrence, new DateOnly(2026, 10, 12)),
        };

        // Google 側も除外を持つようになった
        var response = Json("""
            {
              "id": "p1", "summary": "週次", "status": "confirmed",
              "start": { "date": "2026-10-05" }, "end": { "date": "2026-10-06" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;VALUE=DATE:20261012"]
            }
            """);

        var after = EventMapper.FromGoogle(response, "primary", withLocal);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;EXDATE=20261012", after.Recurrence);
    }

    [Fact]
    public void Googleが外した除外日は手元に残さない()
    {
        // 前に Google から受け取った姿が 10/12 を除外していた。いま届いた姿では外れている
        var received = EventMapper.FromGoogle(Json("""
            {
              "id": "p1", "summary": "週次", "status": "confirmed",
              "start": { "date": "2026-10-05" }, "end": { "date": "2026-10-06" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;VALUE=DATE:20261012"]
            }
            """), "primary");

        var after = EventMapper.FromGoogle(Json(WeeklyParent), "primary", received);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", after.Recurrence);
    }

    // ------------------------------------------------------------------
    // 時刻つきの EXDATE は、系列の開始時刻を変えたら時刻を合わせ直す
    // ------------------------------------------------------------------

    private const string SeriesWithTimedExdate = """
        {
          "id": "x1", "summary": "朝会", "status": "confirmed",
          "start": { "dateTime": "2026-09-28T09:00:00+09:00", "timeZone": "Asia/Tokyo" },
          "end":   { "dateTime": "2026-09-28T09:30:00+09:00", "timeZone": "Asia/Tokyo" },
          "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Asia/Tokyo:20261005T090000"]
        }
        """;

    [Fact]
    public void 開始時刻を変えたらEXDATEの時刻を新しい開始時刻に合わせる()
    {
        var value = EventMapper.FromGoogle(Json(SeriesWithTimedExdate), "primary");

        var changed = value with
        {
            StartTime = value.StartTime!.Value.AddHours(1),
            EndTime = value.EndTime!.Value.AddHours(1),
        };

        var lines = LinesOf(EventMapper.ToGoogle(changed));

        // 日付はそのまま、時刻だけ 10:00 に。TZID と書式は保つ
        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Asia/Tokyo:20261005T100000"], lines);
    }

    [Fact]
    public void 開始時刻を変えないときは元のEXDATE行を一字一句そのまま送る()
    {
        var value = EventMapper.FromGoogle(Json(SeriesWithTimedExdate), "primary");

        var lines = LinesOf(EventMapper.ToGoogle(value with { Title = "朝会（改）" }));

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=Asia/Tokyo:20261005T090000"], lines);
    }

    [Fact]
    public void 開始日だけ動かしても時刻が同じならEXDATEはそのまま()
    {
        var value = EventMapper.FromGoogle(Json(SeriesWithTimedExdate), "primary");

        // 日付だけを動かす（時刻は同じ）
        var changed = value with { Date = value.Date.AddDays(7), EndDate = null };

        var lines = LinesOf(EventMapper.ToGoogle(changed));

        Assert.Equal("EXDATE;TZID=Asia/Tokyo:20261005T090000", lines[1]);
    }

    [Fact]
    public void RRULEも開始時刻も変えたときは新しいRRULEと時刻を直したEXDATEを送る()
    {
        var value = EventMapper.FromGoogle(Json(SeriesWithTimedExdate), "primary");

        var changed = value with
        {
            Recurrence = "FREQ=WEEKLY;BYDAY=TU",
            StartTime = value.StartTime!.Value.AddHours(2),
            EndTime = value.EndTime!.Value.AddHours(2),
        };

        var lines = LinesOf(EventMapper.ToGoogle(changed));

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=TU", "EXDATE;TZID=Asia/Tokyo:20261005T110000"], lines);
    }

    [Fact]
    public void 終日のEXDATEは開始時刻を変えても触らない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "x2", "summary": "朝会", "status": "confirmed",
              "start": { "dateTime": "2026-09-28T09:00:00+09:00", "timeZone": "Asia/Tokyo" },
              "end":   { "dateTime": "2026-09-28T09:30:00+09:00", "timeZone": "Asia/Tokyo" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;VALUE=DATE:20261005"]
            }
            """), "primary");

        var changed = value with
        {
            StartTime = value.StartTime!.Value.AddHours(1),
            EndTime = value.EndTime!.Value.AddHours(1),
        };

        Assert.Equal("EXDATE;VALUE=DATE:20261005", LinesOf(EventMapper.ToGoogle(changed))[1]);
    }

    [Fact]
    public void 複数の時刻つきEXDATEと末尾のZも書式を保って直す()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "x3", "summary": "朝会", "status": "confirmed",
              "start": { "dateTime": "2026-09-28T09:00:00+09:00", "timeZone": "Asia/Tokyo" },
              "end":   { "dateTime": "2026-09-28T09:30:00+09:00", "timeZone": "Asia/Tokyo" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=MO",
                             "EXDATE;TZID=Asia/Tokyo:20261005T090000,20261012T090000",
                             "EXDATE:20261019T000000Z"]
            }
            """), "primary");

        var changed = value with
        {
            StartTime = value.StartTime!.Value.AddHours(1),
            EndTime = value.EndTime!.Value.AddHours(1),
        };

        var lines = LinesOf(EventMapper.ToGoogle(changed));

        Assert.Equal("EXDATE;TZID=Asia/Tokyo:20261005T100000,20261012T100000", lines[1]);

        // UTC（Z）の時刻は、新しい開始時刻を UTC で表した時刻にする（01:00Z → 10:00 JST は 01:00Z）
        Assert.Equal("EXDATE:20261019T010000Z", lines[2]);
    }
}
