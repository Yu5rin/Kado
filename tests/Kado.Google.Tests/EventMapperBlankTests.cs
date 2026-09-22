using System.Text.Json;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

/// <summary>
/// 「無い」と「空」を同じものとして扱えているか。
/// <para>
/// Google は中身の無い項目を<b>返さない</b>。繰り返しでない予定に <c>recurrence</c> は付かず、
/// 場所や説明が空なら項目ごと落ちる。こちらは「繰り返さない」を空の配列、「空欄」を null で
/// 表すので、そのまま比べると毎回「変わった」と判定して送り返してしまう。
/// </para>
/// <para>
/// 実機に近い応答でテストを書いたときに見つかった。それまでは応答に
/// <c>"recurrence": []</c> を書いていたので気づけなかった。
/// </para>
/// </summary>
public class EventMapperBlankTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void 繰り返しでない予定を毎回送り返さない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e20", "summary": "定例", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.False(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void 空欄の項目を毎回送り返さない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e21", "summary": "打ち合わせ", "status": "confirmed",
              "start": { "dateTime": "2026-09-24T09:00:00+09:00" },
              "end":   { "dateTime": "2026-09-24T10:00:00+09:00" }
            }
            """), "primary");

        Assert.Null(value.Location);
        Assert.Null(value.Note);
        Assert.False(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void 空と無いを同じとみなしても変更は見落とさない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e22", "summary": "定例", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        // 空だったものに中身を入れたら、ちゃんと送る
        Assert.True(EventMapper.NeedsPush(value with { Location = "第2会議室" }));
        Assert.True(EventMapper.NeedsPush(value with { Recurrence = "FREQ=WEEKLY" }));
        Assert.True(EventMapper.NeedsPush(value with { Note = "持ち物あり" }));
    }

    [Fact]
    public void 中身があったものを空にしたら送る()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e23", "summary": "定例", "status": "confirmed",
              "location": "第2会議室",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.False(EventMapper.NeedsPush(value));

        // 場所を消したなら、消したことを伝える必要がある
        Assert.True(EventMapper.NeedsPush(value with { Location = null }));
    }

    [Fact]
    public void メールから起こされた予約は送り返さない()
    {
        // 美容室やホテルの予約は Gmail から作られ、Google 側では内容を変えられない。
        // 送ると毎回断られ、そのたびに「一部を伝えられません」と出ていた
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e30", "summary": "HotPepper Beauty のサロン予約", "status": "confirmed",
              "eventType": "fromGmail", "locked": true,
              "start": { "dateTime": "2026-09-24T10:00:00+09:00" },
              "end": { "dateTime": "2026-09-24T11:00:00+09:00" }
            }
            """), "primary");

        Assert.False(EventMapper.NeedsPush(value));

        // 題を変えても送らない。向こうが受け付けないことに変わりはない
        Assert.False(EventMapper.NeedsPush(value with { Title = "別の名前" }));
    }

    [Fact]
    public void 誕生日も送り返さない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e31", "summary": "誕生日", "status": "confirmed",
              "eventType": "birthday",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.False(EventMapper.NeedsPush(value with { Title = "変えてみる" }));
    }

    [Fact]
    public void ふつうの予定は変えたら送る()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e32", "summary": "打ち合わせ", "status": "confirmed",
              "start": { "dateTime": "2026-09-24T10:00:00+09:00" },
              "end": { "dateTime": "2026-09-24T11:00:00+09:00" }
            }
            """), "primary");

        Assert.True(EventMapper.NeedsPush(value with { Title = "打ち合わせ（変更）" }));
    }

    // ------------------------------------------------------------------
    // 表せない繰り返し（RDATE・EXRULE・RRULE 2本以上など）を、空の繰り返しと
    // 取り違えて Google 側の繰り返しを消してしまわないか
    // ------------------------------------------------------------------

    [Fact]
    public void RDATE付きの予定は送り返さない()
    {
        // RecurrenceConverter.FromGoogle が読めず Recurrence は null になるが、
        // それは「繰り返しが無い」のではなく「表せないものを預かっている」だけ
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e40", "summary": "不定期の集まり", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH", "RDATE;VALUE=DATE:20261008"]
            }
            """), "primary");

        Assert.Null(value.Recurrence);
        Assert.False(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void RDATE付きの予定を書き戻すとき本文にrecurrenceキーを入れない()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e41", "summary": "不定期の集まり", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH", "RDATE;VALUE=DATE:20261008"]
            }
            """), "primary");

        // 入れなければ patch は recurrence に触らない。空の配列を入れると
        // 「繰り返しを外す」意味になり、Google 側の繰り返しが消えてしまう
        Assert.False(EventMapper.ToGoogle(value).ContainsKey("recurrence"));
    }

    [Fact]
    public void 本当に繰り返しを外した予定は空の配列を送る()
    {
        // 元は RRULE 1本（表せる）で受け取っていたが、こちらで単発に変えた場面
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e42", "summary": "週次レビュー", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" },
              "recurrence": ["RRULE:FREQ=WEEKLY;BYDAY=TH"]
            }
            """), "primary") with { Recurrence = null };

        var body = EventMapper.ToGoogle(value);

        Assert.True(body.ContainsKey("recurrence"));
        Assert.Empty(body["recurrence"]!.AsArray());
        Assert.True(EventMapper.NeedsPush(value));
    }

    [Fact]
    public void 繰り返しを持たない予定はこれまでどおり()
    {
        var value = EventMapper.FromGoogle(Json("""
            {
              "id": "e43", "summary": "定例", "status": "confirmed",
              "start": { "date": "2026-09-24" }, "end": { "date": "2026-09-25" }
            }
            """), "primary");

        Assert.Null(value.Recurrence);

        var body = EventMapper.ToGoogle(value);
        Assert.True(body.ContainsKey("recurrence"));
        Assert.Empty(body["recurrence"]!.AsArray());
        Assert.False(EventMapper.NeedsPush(value));
    }
}
