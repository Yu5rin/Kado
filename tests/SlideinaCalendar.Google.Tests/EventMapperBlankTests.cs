using System.Text.Json;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Tests;

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
}
