using Kado.Data.Models;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>プロセス全体の <c>TZ</c> を書き換えるテストの置き場。ほかのテストと並べて走らせない。</summary>
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
    private readonly string? _originalZone = Environment.GetEnvironmentVariable("TZ");

    private static void UseZone(string id)
    {
        Environment.SetEnvironmentVariable("TZ", id);
        TimeZoneInfo.ClearCachedData();
    }

    public void Dispose()
    {
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

    [Fact]
    public void キャッシュを捨てると端末の新しいタイムゾーンを読む()
    {
        UseZone("Asia/Tokyo");
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
        UseZone("Asia/Tokyo");
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

        UseZone("America/Los_Angeles");
        ClockCaches.Refresh();
        vm.OnClockChanged(new DateTime(2026, 9, 24, 13, 0, 0));

        Assert.Contains(vm.SelectedDay.Tasks, t => t.Title == "提出");
    }
}
