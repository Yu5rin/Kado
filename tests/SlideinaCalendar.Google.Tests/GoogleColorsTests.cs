using System.Text.Json;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// 色番号と実際の色の行き来。
/// <para>
/// 対応表は Google から取る。こちらで書き写すと、向こうが色を調整したときに食い違う。
/// </para>
/// </summary>
public class GoogleColorsTests
{
    /// <summary>colors.get が返す形。実際の色の値そのものには依存しない。</summary>
    private const string Response = """
        {
          "kind": "calendar#colors",
          "calendar": {
            "1":  { "background": "#ac725e", "foreground": "#1d1d1d" },
            "2":  { "background": "#d06b64", "foreground": "#1d1d1d" },
            "9":  { "background": "#4986e7", "foreground": "#1d1d1d" },
            "17": { "background": "#9a9cff", "foreground": "#1d1d1d" }
          },
          "event": {
            "1": { "background": "#a4bdfc", "foreground": "#1d1d1d" },
            "11": { "background": "#dc2127", "foreground": "#1d1d1d" }
          }
        }
        """;

    private static GoogleColors Read() => GoogleColors.Read(JsonDocument.Parse(Response).RootElement);

    [Fact]
    public void 対応表を読める()
    {
        var colors = Read();

        Assert.Equal(4, colors.Calendar.Count);
        Assert.Equal(2, colors.Event.Count);
        Assert.Equal("#4986e7", colors.CalendarColor("9"));
        Assert.False(colors.IsEmpty);
    }

    [Fact]
    public void ぴたり同じ色はその番号になる()
    {
        Assert.Equal("9", Read().ClosestCalendarId("#4986e7"));
    }

    [Fact]
    public void 少しずれた色はいちばん近い番号に寄る()
    {
        // 青系。ぴたりの値は表に無い
        Assert.Equal("9", Read().ClosestCalendarId("#4a87e8"));
    }

    [Fact]
    public void 赤は赤に寄り青には寄らない()
    {
        var id = Read().ClosestCalendarId("#d2201f");

        Assert.Equal("2", id);
    }

    [Fact]
    public void 先頭の記号は付いていてもいなくてもよい()
    {
        var colors = Read();

        Assert.Equal(colors.ClosestCalendarId("#4986e7"), colors.ClosestCalendarId("4986e7"));
    }

    [Fact]
    public void 読めない色は諦める()
    {
        var colors = Read();

        // 変な値で適当な番号を返すと、意図しない色に塗り替わる
        Assert.Null(colors.ClosestCalendarId(null));
        Assert.Null(colors.ClosestCalendarId(string.Empty));
        Assert.Null(colors.ClosestCalendarId("みどり"));
        Assert.Null(colors.ClosestCalendarId("#12345"));
    }

    [Fact]
    public void 表が空なら寄せ先を決めない()
    {
        // 取れなかったときに色を書き戻すと、思わぬ色になる
        Assert.Null(GoogleColors.Empty.ClosestCalendarId("#4986e7"));
        Assert.True(GoogleColors.Empty.IsEmpty);
    }

    [Fact]
    public void 予定の色は別の表から選ぶ()
    {
        var colors = Read();

        // カレンダー用と予定用で番号の意味が違う
        Assert.Equal("11", colors.ClosestEventId("#dc2127"));
        Assert.Equal("1", colors.ClosestEventId("#a4bdfc"));
    }

    [Fact]
    public void 壊れた応答でも落ちない()
    {
        var colors = GoogleColors.Read(JsonDocument.Parse("""{"calendar":{"1":{}},"event":[]}""").RootElement);

        Assert.True(colors.IsEmpty);
    }

    [Fact]
    public void 中身が無くても落ちない()
    {
        Assert.True(GoogleColors.Read(JsonDocument.Parse("{}").RootElement).IsEmpty);
    }
}
