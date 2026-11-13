using System.Text.Json;
using Kado.Core;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

/// <summary>
/// 端末のタイムゾーンの差し替え（<see cref="LocalZone"/>）を使うテストの置き場。ほかのテストと並べて走らせない。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTimeZoneCollection
{
    public const string Name = "端末のタイムゾーンを書き換える";
}

/// <summary>
/// PC のタイムゾーンと予定のタイムゾーンが違うときの、例外回の除外日。
/// <para>
/// 開始日は PC の時刻に直して持つ。除外日（元の回の日）も同じ基準で取らないと、
/// 親の回と例外回が噛み合わず、動かした回が元の日にも出る。
/// </para>
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class EventMapperTimeZoneTests
{
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.CreateCustomTimeZone("Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void 元の回の日はPCの時刻に直した日付で取る()
    {
        // ロサンゼルスの 10/1 20:00（夏時間 -07:00）は、東京では 10/2 12:00
        var item = Json("""
            {
              "id": "g1_20261002T030000Z",
              "recurringEventId": "g1",
              "originalStartTime": { "dateTime": "2026-10-01T20:00:00-07:00", "timeZone": "America/Los_Angeles" },
              "start": { "dateTime": "2026-10-01T21:00:00-07:00" },
              "end": { "dateTime": "2026-10-01T22:00:00-07:00" }
            }
            """);

        using var _ = LocalZone.OverrideForTest(Tokyo);

        var original = EventMapper.OriginalStartDateOf(item);
        var moved = EventMapper.FromGoogle(item, "cal-a");

        Assert.Equal(new DateOnly(2026, 10, 2), original);

        // 開始日と同じ基準（東京の 10/2 13:00）
        Assert.Equal(new DateOnly(2026, 10, 2), moved.Date);
    }
}
