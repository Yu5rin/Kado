using System.Text.Json.Nodes;
using System.Text.Json;
using Kado.Data.Models;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

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

    /// <summary>
    /// この PC の時差を付けた時刻の文字列。
    /// <para>
    /// 予定は「この PC で見た時刻」として読む。テストに <c>+09:00</c> と書き込むと、
    /// 時差の違う場所（CI は UTC）で動かしたときに落ちる。動かす場所に合わせて作る。
    /// </para>
    /// </summary>
    private static string LocalTime(int year, int month, int day, int hour, int minute)
    {
        var naive = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(naive);

        return new DateTimeOffset(naive, offset).ToString("yyyy-MM-ddTHH:mm:sszzz");
    }

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
              "start": { "dateTime": "{{START}}" },
              "end":   { "dateTime": "{{END}}" }
            }
            """.Replace("{{START}}", LocalTime(2026, 9, 24, 9, 0))
               .Replace("{{END}}", LocalTime(2026, 9, 24, 10, 30))), "primary");

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
              "start": { "dateTime": "{{START}}" },
              "end":   { "dateTime": "{{END}}" }
            }
            """.Replace("{{START}}", LocalTime(2026, 9, 24, 22, 0))
               .Replace("{{END}}", LocalTime(2026, 9, 25, 6, 0))), "primary");

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
    public void 予定ごとの通知の指定を引き継ぐ()
    {
        // Google には無い、こちら独自の項目。引き継がないと、PushChangesAsync が
        // PATCH の応答をそのまま FromGoogle に通すたびに消える
        const string source = """
            {
              "id": "e-notify", "summary": "定例",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """;

        var existing = new CalendarEvent { Id = "元の予定", Notify = true };
        var notified = EventMapper.FromGoogle(Json(source), "primary", existing);

        Assert.Equal(true, notified.Notify);

        var existingOff = new CalendarEvent { Id = "元の予定", Notify = false };
        var silenced = EventMapper.FromGoogle(Json(source), "primary", existingOff);

        Assert.Equal(false, silenced.Notify);

        // 初回の取り込みでは、こちらにまだ指定が無いので null のまま
        var first = EventMapper.FromGoogle(Json(source), "primary");
        Assert.Null(first.Notify);
    }

    [Fact]
    public void 時刻つきの予定にはtimeZoneを添える()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        // 変換できない環境（実機では通常 Windows で成功する）では添えない、が
        // どちらの場合でも「クラッシュしない」「終日には絶対に付かない」ことは保証する
        var expected = ExpectedIanaZone();

        if (expected is null)
        {
            Assert.False(body["start"]!.AsObject().ContainsKey("timeZone"));
            Assert.False(body["end"]!.AsObject().ContainsKey("timeZone"));
            return;
        }

        Assert.Equal(expected, body["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal(expected, body["end"]!["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public void 終日予定にはtimeZoneを添えない()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24),
        });

        Assert.False(body["start"]!.AsObject().ContainsKey("timeZone"));
        Assert.False(body["end"]!.AsObject().ContainsKey("timeZone"));
    }

    [Fact]
    public void 繰り返しでも単発でも同じくtimeZoneを添える()
    {
        // 繰り返しの有無で出し分けない。単純さのほうを取った
        var recurring = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "週次", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
            Recurrence = "FREQ=WEEKLY;BYDAY=TH",
        });

        var single = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e2", Title = "単発", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        });

        var hasZone = recurring["start"]!.AsObject().ContainsKey("timeZone");
        Assert.Equal(hasZone, single["start"]!.AsObject().ContainsKey("timeZone"));
    }

    /// <summary>本番のコードと同じ手順で、この環境で得られるはずの IANA 名を求める。</summary>
    private static string? ExpectedIanaZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    }

    [Fact]
    public void timeZoneを足しても毎回は送り返さない()
    {
        // 控えに timeZone が無い状態から始めても、書き戻す本文に新しく添えるだけで
        // 「変わった」とは判定しない。SameMoment は date / dateTime しか見ないので、
        // timeZone の有無やキーの追加は比較に影響しない
        var withoutZone = EventMapper.FromGoogle(Json("""
            {
              "id": "e11", "summary": "定例",
              "start": { "dateTime": "{{START}}" },
              "end":   { "dateTime": "{{END}}" }
            }
            """.Replace("{{START}}", LocalTime(2026, 9, 24, 9, 0))
               .Replace("{{END}}", LocalTime(2026, 9, 24, 10, 0))), "primary");

        Assert.False(EventMapper.NeedsPush(withoutZone));

        // 控えの側に timeZone が入っていても同じ
        var withZone = EventMapper.FromGoogle(Json("""
            {
              "id": "e11", "summary": "定例",
              "start": { "dateTime": "{{START}}", "timeZone": "Asia/Tokyo" },
              "end":   { "dateTime": "{{END}}", "timeZone": "Asia/Tokyo" }
            }
            """.Replace("{{START}}", LocalTime(2026, 9, 24, 9, 0))
               .Replace("{{END}}", LocalTime(2026, 9, 24, 10, 0))), "primary");

        Assert.False(EventMapper.NeedsPush(withZone));

        // それでも本当に時刻を変えれば、ちゃんと検知する
        Assert.True(EventMapper.NeedsPush(withoutZone with { StartTime = new TimeOnly(9, 30) }));
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

    // ------------------------------------------------------------------
    // 他人が主催する予定は Google 側で変えられない
    //
    // organizer.self が明示的に false で、かつ guestsCanModify が true でなければ、
    // こちらで変えても保存はできるのに向こうへは伝わらない
    // ------------------------------------------------------------------

    private static CalendarEvent WithRaw(string raw) => new()
    {
        Id = "e1", Title = "会議", GoogleRaw = raw,
    };

    [Fact]
    public void 他人が主催する予定は変えられない()
    {
        var value = WithRaw("""{"id":"g1","organizer":{"self":false}}""");

        Assert.True(EventMapper.IsLocked(value));
    }

    [Fact]
    public void guestsCanModifyが立っていれば他人主催でも変えられる()
    {
        var value = WithRaw(
            """{"id":"g1","organizer":{"self":false},"guestsCanModify":true}""");

        Assert.False(EventMapper.IsLocked(value));
    }

    [Fact]
    public void 自分が主催する予定は変えられる()
    {
        var value = WithRaw("""{"id":"g1","organizer":{"self":true}}""");

        Assert.False(EventMapper.IsLocked(value));
    }

    [Fact]
    public void organizerが無ければ自分の予定として扱う()
    {
        // 自分の予定にも organizer が省略されることがある。ここまで巻き込むと
        // 自分の予定まで編集できなくなる
        Assert.False(EventMapper.IsLocked(WithRaw("""{"id":"g1"}""")));
        Assert.False(EventMapper.IsLocked(WithRaw("""{"id":"g1","organizer":{}}""")));
    }

    [Fact]
    public void 他人が主催する予定は書き戻さない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e12", "summary": "会議",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "organizer": { "self": false }
            }
            """), "primary");

        // 送っても向こうに拒まれるだけ。毎回「一部を伝えられません」を出さないために送らない
        Assert.False(EventMapper.NeedsPush(value with { Title = "会議（変更）" }));
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

    // ------------------------------------------------------------------
    // 送る範囲は必ず「開始より後に終わる」
    //
    // Google は長さ 0 の範囲も逆転した範囲も 400 で断る。実機で、送れない予定が
    // 1件あるだけでそのカレンダーの同期が丸ごと止まった
    // ------------------------------------------------------------------

    /// <summary>送る本文から終了の時刻を読む。</summary>
    private static DateTimeOffset EndOf(JsonObject body) =>
        DateTimeOffset.Parse(
            body["end"]!["dateTime"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset StartOf(JsonObject body) =>
        DateTimeOffset.Parse(
            body["start"]!["dateTime"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void 終了時刻の無い予定は1時間として送る()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "打ち合わせ", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0),
        });

        // 開始と同じ時刻で送ると Google が断る
        Assert.Equal(StartOf(body).AddHours(1), EndOf(body));
    }

    [Fact]
    public void 終了が開始より前でも後ろに直して送る()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "打ち間違い", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(15, 0), EndTime = new TimeOnly(9, 0),
        });

        Assert.True(EndOf(body) > StartOf(body));
    }

    [Fact]
    public void 終了が開始と同じでも後ろに直して送る()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "長さゼロ", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(9, 0),
        });

        Assert.True(EndOf(body) > StartOf(body));
    }

    [Fact]
    public void まともな終了時刻はそのまま送る()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(23, 0), EndTime = new TimeOnly(23, 55),
        });

        Assert.Equal(StartOf(body).AddMinutes(55), EndOf(body));
    }

    [Fact]
    public void 日をまたぐ予定は終了日のほうで表す()
    {
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "夜勤", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(22, 0),
            EndDate = D(2026, 9, 25), EndTime = new TimeOnly(6, 0),
        });

        // 時刻だけ見れば逆転しているが、日が進んでいるので直してはいけない
        Assert.Equal(StartOf(body).AddHours(8), EndOf(body));
    }

    // ------------------------------------------------------------------
    // 除外日（EXDATE）は同期の内部事情。使う人が変えたのでない限り送り返さない
    // ------------------------------------------------------------------

    [Fact]
    public void 除外日はGoogleへ送り返さない()
    {
        // ExcludeFromParent が内部で足す形（RRULE のあとに ;EXDATE=... が続く）
        var body = EventMapper.ToGoogle(new CalendarEvent
        {
            Id = "e1", Title = "週次レビュー", Date = D(2026, 9, 24),
            Recurrence = "FREQ=WEEKLY;BYDAY=TH;EXDATE=20261001",
        });

        var line = body["recurrence"]![0]!.GetValue<string>();
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=TH", line);
        Assert.DoesNotContain("EXDATE", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 除外日だけの変更ではNeedsPushがtrueにならない()
    {
        var original = """
            {
              "id": "e9", "summary": "週次レビュー",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH"]
            }
            """;

        var value = EventMapper.FromGoogle(Json(original), "primary");

        // ExcludeFromParent が足す形。中身（タイトル等）は変えていない
        var withExdate = value with { Recurrence = "FREQ=WEEKLY;BYDAY=TH;EXDATE=20261001" };

        Assert.False(EventMapper.NeedsPush(withExdate));
    }

    // ------------------------------------------------------------------
    // Google 側に本物の EXDATE がある予定（ics 取り込みや他のクライアントが作った
    // 繰り返しは、実際に EXDATE を持つ）。こちらの内部事情（同期の取り込みが足す
    // 除外日）と混ぜず、一字一句そのまま保つ
    // ------------------------------------------------------------------

    private const string RecurrenceWithRealExdate = """
        {
          "id": "g1", "summary": "週次レビュー",
          "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
          "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH", "EXDATE;TZID=Asia/Tokyo:20261001T090000"]
        }
        """;

    private static string[] RecurrenceLinesOf(JsonObject body) =>
        body["recurrence"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void Google側の本物のEXDATEは何も変えなければ送らない()
    {
        var value = EventMapper.FromGoogle(Json(RecurrenceWithRealExdate), "primary");

        Assert.False(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void 題名だけ変えても元のEXDATE行を一字一句そのまま送る()
    {
        var value = EventMapper.FromGoogle(Json(RecurrenceWithRealExdate), "primary");
        var changed = value with { Title = "週次レビュー（変更）" };

        var lines = RecurrenceLinesOf(EventMapper.ToGoogle(changed));

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=TH", "EXDATE;TZID=Asia/Tokyo:20261001T090000"],
            lines);
    }

    [Fact]
    public void RRULEを変えたら新しいRRULEと元のEXDATE行を組み合わせる()
    {
        var value = EventMapper.FromGoogle(Json(RecurrenceWithRealExdate), "primary");

        // 繰り返しの曜日を変えた（木曜 → 金曜）
        var changed = value with { Recurrence = "FREQ=WEEKLY;BYDAY=FR" };

        var lines = RecurrenceLinesOf(EventMapper.ToGoogle(changed));

        Assert.Equal(
            ["RRULE:FREQ=WEEKLY;BYDAY=FR", "EXDATE;TZID=Asia/Tokyo:20261001T090000"],
            lines);
    }

    [Fact]
    public void 内部で足した除外日は元の本物のEXDATEに混ぜて送らない()
    {
        var value = EventMapper.FromGoogle(Json(RecurrenceWithRealExdate), "primary");

        // 別の回（10/8）を例外回として取り込み、EventSyncEngine.ExcludeFromParent が
        // 内部で除外日を足した状態を再現する（RRULE 部分は変わっていない）
        var withLocalExclusion = value with
        {
            Recurrence = RecurrenceConverter.WithExceptionDate(value.Recurrence, new DateOnly(2026, 10, 8)),
        };

        var lines = RecurrenceLinesOf(EventMapper.ToGoogle(withLocalExclusion));

        // 元の本物の EXDATE（10/1）はそのまま。内部で足した 10/8 は入らない
        Assert.Contains("EXDATE;TZID=Asia/Tokyo:20261001T090000", lines);
        Assert.DoesNotContain(lines, l => l.Contains("20261008", StringComparison.Ordinal));
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void GoogleRawが無ければRRULEだけを送る()
    {
        // 新規作成、またはまだ繰り返しでなかった予定
        var value = new CalendarEvent
        {
            Id = "e1", Title = "週次レビュー", Date = D(2026, 9, 24),
            Recurrence = "FREQ=WEEKLY;BYDAY=TH",
        };

        var lines = RecurrenceLinesOf(EventMapper.ToGoogle(value));

        Assert.Equal(["RRULE:FREQ=WEEKLY;BYDAY=TH"], lines);
    }

    // ------------------------------------------------------------------
    // 繰り返しの1回だけの回（子）
    // ------------------------------------------------------------------

    [Fact]
    public void recurringEventIdを持つ回はインスタンスと判定する()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "g1_20261001", "summary": "週次レビュー（振替）",
              "recurringEventId": "g1",
              "start": { "date": "2026-10-02" }, "end": { "date": "2026-10-03" }
            }
            """), "primary");

        Assert.True(EventMapper.IsRecurringInstance(value));
    }

    [Fact]
    public void recurringEventIdを持たない予定はインスタンスでない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "g1", "summary": "週次レビュー",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.False(EventMapper.IsRecurringInstance(value));
    }

    // ------------------------------------------------------------------
    // カレンダーの移動先
    //
    // CalendarId（こちらの希望）は既存があれば引き継ぐ。GoogleCalendarId（Google 側の
    // 実際の場所）は取りに行った先で必ず更新する
    // ------------------------------------------------------------------

    [Fact]
    public void 新規の予定はカレンダーIdと実カレンダーIdが両方取りに行った先になる()
    {
        var value = EventMapper.FromGoogle(Json("""
            {"id": "g1", "summary": "棚卸し", "start": {"date": "2026-09-24"}, "end": {"date": "2026-09-25"}}
            """), "local:shigoto", existing: null, now: null, googleCalendarId: "primary");

        Assert.Equal("local:shigoto", value.CalendarId);
        Assert.Equal("primary", value.GoogleCalendarId);
    }

    [Fact]
    public void 既存の予定のカレンダー希望は取り込みで上書きしない()
    {
        var existing = new CalendarEvent
        {
            Id = "e1", Title = "棚卸し", Date = D(2026, 9, 24),
            CalendarId = "local:private", GoogleEventId = "g1", GoogleCalendarId = "primary",
        };

        // Google 側はまだ元のカレンダー（primary）のまま。こちらは別のカレンダーへ
        // 移すつもりで CalendarId を変えているが、まだ送れていない
        var moved = existing with { CalendarId = "local:kojin" };

        var value = EventMapper.FromGoogle(Json("""
            {"id": "g1", "summary": "棚卸し（相手で変更）",
             "start": {"date": "2026-09-24"}, "end": {"date": "2026-09-25"}}
            """), "primary", moved, googleCalendarId: "primary");

        // 「移すつもり」の希望を、取り込みが元へ戻してしまわない
        Assert.Equal("local:kojin", value.CalendarId);
        Assert.Equal("primary", value.GoogleCalendarId);
    }

    // ------------------------------------------------------------------
    // 添付
    // ------------------------------------------------------------------

    [Fact]
    public void 添付を足していなければattachmentsキーを送らない()
    {
        var value = new CalendarEvent { Id = "e1", Title = "予定", Date = D(2026, 9, 24) };

        var body = EventMapper.ToGoogle(value);

        Assert.False(body.ContainsKey("attachments"));
    }

    [Fact]
    public void 添付を足すとattachmentsキーを送る()
    {
        var attachments = new[]
        {
            new EventAttachment("file1", "https://drive.google.com/file/d/file1/view", "議事録.pdf", "application/pdf"),
        };

        var value = new CalendarEvent
        {
            Id = "e1", Title = "予定", Date = D(2026, 9, 24),
            PendingAttachments = EventMapper.ToPendingAttachmentsJson(attachments),
        };

        var body = EventMapper.ToGoogle(value);

        Assert.True(body.ContainsKey("attachments"));
        Assert.Single(body["attachments"]!.AsArray());
        Assert.Equal("file1", body["attachments"]![0]!["fileId"]!.GetValue<string>());
    }

    [Fact]
    public void 添付の一覧はPendingAttachmentsがあればそちらを優先する()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "g1", "summary": "予定",
              "start": {"date": "2026-09-24"}, "end": {"date": "2026-09-25"},
              "attachments": [
                {"fileId": "old", "fileUrl": "https://drive.google.com/file/d/old/view", "title": "旧.pdf"}
              ]
            }
            """), "primary");

        Assert.Equal("old", Assert.Single(EventMapper.EffectiveAttachments(value)).FileId);

        var pending = value with
        {
            PendingAttachments = EventMapper.ToPendingAttachmentsJson(
            [
                new EventAttachment("new", "https://drive.google.com/file/d/new/view", "新.pdf", "application/pdf"),
            ]),
        };

        Assert.Equal("new", Assert.Single(EventMapper.EffectiveAttachments(pending)).FileId);
    }

    [Fact]
    public void 添付のURLはhttpsだけ読む理由になるfileUrlを持つ()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "g1", "summary": "予定",
              "start": {"date": "2026-09-24"}, "end": {"date": "2026-09-25"},
              "attachments": [
                {"fileId": "a", "fileUrl": "https://drive.google.com/file/d/a/view", "title": "資料.pdf"}
              ]
            }
            """), "primary");

        var attachment = Assert.Single(EventMapper.EffectiveAttachments(value));
        Assert.StartsWith("https://", attachment.FileUrl, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // ゲスト・会議・出欠・仮の予定は受け取って持つだけ。送る本文には絶対に含めない
    // ------------------------------------------------------------------

    [Fact]
    public void 書き戻す本文にattendeesとconferenceDataとresponseStatusは含めない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "g1", "summary": "会議",
              "start": {"date": "2026-09-24"}, "end": {"date": "2026-09-25"},
              "attendees": [{"email": "a@example.com", "responseStatus": "accepted"}],
              "conferenceData": {"conferenceId": "abc", "entryPoints": [{"uri": "https://meet.example.com/abc"}]},
              "hangoutLink": "https://meet.example.com/abc",
              "colorId": "7",
              "transparency": "transparent"
            }
            """), "primary");

        var body = EventMapper.ToGoogle(value with { Title = "会議（変更）" });

        Assert.False(body.ContainsKey("attendees"));
        Assert.False(body.ContainsKey("conferenceData"));
        Assert.False(body.ContainsKey("hangoutLink"));
        Assert.False(body.ContainsKey("colorId"));
        Assert.False(body.ContainsKey("responseStatus"));
        Assert.False(body.ContainsKey("transparency"));

        // 受け取った生データには残っている（読むだけなら安全、要件書どおり）
        Assert.Contains("attendees", value.GoogleRaw, StringComparison.Ordinal);
        Assert.Contains("conferenceData", value.GoogleRaw, StringComparison.Ordinal);
    }

    [Fact]
    public void イベント個別のcolorIdは取り込まない()
    {
        var existing = new CalendarEvent { Id = "e1", Title = "予定", Date = D(2026, 9, 24), Color = "#336699" };

        var value = EventMapper.FromGoogle(Json("""
            {"id": "g1", "summary": "予定", "start": {"date": "2026-09-24"},
             "end": {"date": "2026-09-25"}, "colorId": "11"}
            """), "primary", existing);

        // 色はカレンダーで決まる。イベント個別の colorId で上書きしない
        Assert.Equal("#336699", value.Color);
    }
}
