using Kado.Core;
using Kado.Data.Models;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>端末のタイムゾーンを書き換える（<c>TZ</c> や <see cref="LocalZone"/> の差し替え）テストの置き場。ほかのテストと並べて走らせない。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTimeZoneCollection
{
    public const string Name = "端末のタイムゾーンを書き換える";
}

/// <summary>
/// 時計・タイムゾーンが変わったあとの動き。
/// <para>
/// .NET は <c>TimeZoneInfo.Local</c> を最初に読んだ値のまま持つ。何日も動き続けるアプリは、
/// 時計の変更の知らせを受けたらキャッシュを捨て、時刻と表示を読み直す。
/// </para>
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class ClockCachesTests : IDisposable
{
    // 東京（UTC+9）とロサンゼルスの夏時間（UTC-7）。アプリの動きを確かめるテストは、
    // OS に頼らず固定のゾーンを作って、端末のタイムゾーンの入口（LocalZone）を差し替える
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.CreateCustomTimeZone("Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");
    private static readonly TimeZoneInfo LosAngeles =
        TimeZoneInfo.CreateCustomTimeZone("LosAngeles", TimeSpan.FromHours(-7), "LosAngeles", "LosAngeles");

    private readonly string? _originalZone = Environment.GetEnvironmentVariable("TZ");
    private IDisposable? _zone;

    /// <summary>端末のタイムゾーンを、<c>TZ</c> を使わずに <see cref="LocalZone"/> の差し替えで切り替える。</summary>
    private void UseZone(TimeZoneInfo zone)
    {
        // 前の差し替えを先に戻してから差し替える。Dispose の戻し先が、いつも元の状態になる
        _zone?.Dispose();
        _zone = LocalZone.OverrideForTest(zone);
    }

    public void Dispose()
    {
        _zone?.Dispose();
        // .NET の仕組みを確かめるテストが書き換えた TZ を戻す（TZ を触らなかったときは何も変わらない）
        Environment.SetEnvironmentVariable("TZ", _originalZone);
        TimeZoneInfo.ClearCachedData();
    }

    [Theory]
    [InlineData(ClockChange.TimeChanged, true)]
    [InlineData(ClockChange.Resume, true)]
    [InlineData(ClockChange.Suspend, false)]
    [InlineData(ClockChange.PowerStatus, false)]
    public void 時計の変更と復帰のときだけキャッシュを捨てる(ClockChange change, bool expected) =>
        Assert.Equal(expected, ClockChangeRules.ShouldRefreshCaches(change));

    [LinuxOnlyFact]
    public void キャッシュを捨てると端末の新しいタイムゾーンを読む()
    {
        // .NET の仕組みそのものの確認。TZ を読むのは Linux の .NET だけなので、LocalZone は使わない
        Environment.SetEnvironmentVariable("TZ", "Asia/Tokyo");
        TimeZoneInfo.ClearCachedData();
        Assert.Equal(TimeSpan.FromHours(9), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 24, 12, 0, 0)));

        // 環境が変わっても、キャッシュを捨てるまでは古いまま
        Environment.SetEnvironmentVariable("TZ", "America/Los_Angeles");
        Assert.Equal(TimeSpan.FromHours(9), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 24, 12, 0, 0)));

        ClockCaches.Refresh();

        Assert.Equal(TimeSpan.FromHours(-7), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 24, 12, 0, 0)));
    }

    [Fact]
    public void 時計が変わったあとは日付が同じでも表示を読み直す()
    {
        UseZone(Tokyo);
        using var test = TestWorkspace.Create();

        // 2026-09-24 20:00 UTC に完了。東京では 9/25、ロサンゼルスでは 9/24 の出来事
        test.Workspace.AddTask(new TaskItem
        {
            Id = "t1", Title = "提出", IsDone = true,
            CompletedAt = new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero),
        });

        var vm = new MainViewModel(test.Workspace, new DateOnly(2026, 9, 24));
        vm.SelectedDate = new DateOnly(2026, 9, 24);
        Assert.DoesNotContain(vm.SelectedDay.Tasks, t => t.Title == "提出");

        UseZone(LosAngeles);
        ClockCaches.Refresh();
        vm.OnClockChanged(new DateTime(2026, 9, 24, 13, 0, 0));

        Assert.Contains(vm.SelectedDay.Tasks, t => t.Title == "提出");
    }
}
