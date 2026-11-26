using System.Text.Json;
using Kado.Core;
using Kado.Google.Mapping;

namespace Kado.Google.Tests;

/// <summary>
/// 末尾 Z（UTC）の EXDATE は、この PC の時刻に直してから日付を取る。
/// <para>UTC の日付のまま使うと、日本では朝9時より前の回の除外が前日にずれ、別の回を除いてしまう。</para>
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class RecurrenceExdateZoneTests
{
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.CreateCustomTimeZone("Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");

    [Fact]
    public void 末尾ZのEXDATEはこのPCの時刻に直した日付を除外日にする()
    {
        using var _ = LocalZone.OverrideForTest(Tokyo);

        // 日本時間の 9/22 08:00。UTC では 9/21 23:00
        var spec = RecurrenceConverter.FromGoogle(["RRULE:FREQ=DAILY", "EXDATE:20260921T230000Z"]);

        Assert.Equal("FREQ=DAILY;EXDATE=20260922", spec);
    }

    [Fact]
    public void 末尾Zが無い時刻つきのEXDATEは書かれた日付のまま使う()
    {
        using var _ = LocalZone.OverrideForTest(Tokyo);

        var spec = RecurrenceConverter.FromGoogle(["RRULE:FREQ=DAILY", "EXDATE;TZID=Asia/Tokyo:20260921T090000"]);

        Assert.Equal("FREQ=DAILY;EXDATE=20260921", spec);
    }

    [Fact]
    public void 送り返すEXDATEは元の文字列のまま変えない()
    {
        using var _ = LocalZone.OverrideForTest(Tokyo);

        var original = new[] { "RRULE:FREQ=DAILY", "EXDATE:20260921T230000Z" };
        var local = RecurrenceConverter.FromGoogle(original);

        var outgoing = RecurrenceConverter.BuildOutgoing(local, original);

        Assert.Equal(original, outgoing);
    }
}
