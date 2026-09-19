using System.Text.Json;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// 差分の判定に使う正規化（要件書 6.3）。
/// <para>キーの並び順で「変わった」と誤判定すると、毎回の同期で書き戻しが走る。</para>
/// </summary>
public class GoogleJsonTests
{
    [Fact]
    public void キーの並び順が違っても同じ内容とみなす()
    {
        Assert.True(GoogleJson.SameContent(
            """{"summary":"会議","id":"e1","location":"第2会議室"}""",
            """{"id":"e1","location":"第2会議室","summary":"会議"}"""));
    }

    [Fact]
    public void 入れ子の並び順も見ない()
    {
        Assert.True(GoogleJson.SameContent(
            """{"start":{"dateTime":"2026-09-24T09:00:00+09:00","timeZone":"Asia/Tokyo"}}""",
            """{"start":{"timeZone":"Asia/Tokyo","dateTime":"2026-09-24T09:00:00+09:00"}}"""));
    }

    [Fact]
    public void 中身が違えば違うとみなす()
    {
        Assert.False(GoogleJson.SameContent(
            """{"summary":"会議"}""",
            """{"summary":"会議（変更）"}"""));
    }

    [Fact]
    public void 配列の順番は意味を持つので並べ替えない()
    {
        // recurrence の行は順番そのものが意味を持つ
        Assert.False(GoogleJson.SameContent(
            """{"recurrence":["RRULE:FREQ=DAILY","EXDATE:20260921"]}""",
            """{"recurrence":["EXDATE:20260921","RRULE:FREQ=DAILY"]}"""));
    }

    [Fact]
    public void 壊れていたら違うとみなす()
    {
        // 取り直せば直る。ここで落とさない
        Assert.False(GoogleJson.SameContent("""{"a":1}""", "これはJSONではない"));
    }

    [Fact]
    public void 控えは並べ替えた形で作る()
    {
        using var document = JsonDocument.Parse("""{"b":2,"a":1}""");

        Assert.Equal("""{"a":1,"b":2}""", GoogleJson.Normalize(document.RootElement));
    }

    [Fact]
    public void 無い項目はnullで受ける()
    {
        using var document = JsonDocument.Parse("""{"summary":"会議","location":""}""");
        var root = document.RootElement;

        Assert.Equal("会議", root.Text("summary"));
        Assert.Null(root.Text("description"));

        // 空文字は「入っていない」と同じに扱う。空で上書きすると消える
        Assert.Null(root.Text("location"));
    }

    [Fact]
    public void 時刻を読める()
    {
        using var document = JsonDocument.Parse("""{"updated":"2026-09-24T09:00:00.000Z","broken":"あ"}""");
        var root = document.RootElement;

        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), root.Timestamp("updated"));
        Assert.Null(root.Timestamp("broken"));
        Assert.Null(root.Timestamp("missing"));
    }
}
