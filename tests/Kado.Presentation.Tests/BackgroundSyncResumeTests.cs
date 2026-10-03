using Microsoft.Extensions.Time.Testing;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// 裏の同期が、スリープから戻ったあとにすぐ追いつくこと。手で同期が通ったら、失敗で延びた間隔が戻ること。
/// <para>時計を進めて試すので、実時間は待たない。</para>
/// </summary>
public class BackgroundSyncResumeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static async Task WaitUntilAsync(Func<bool> until)
    {
        var deadline = Environment.TickCount64 + 5000;

        while (!until() && Environment.TickCount64 < deadline) await Task.Delay(5);
    }

    /// <summary>
    /// 条件が満たされるまで、時計を1秒ずつ進める。何秒進めたかを返す。
    /// <para>
    /// 待ち直しは別の流れで行われる。進めすぎず、待ち直しの印（タイマー）が置かれたあとで進むよう、
    /// 1秒ずつ進めては、少し待つ。
    /// </para>
    /// </summary>
    private static async Task<int> AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> until, int maxSeconds)
    {
        var steps = 0;

        while (!until() && steps < maxSeconds)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            steps++;
            await Task.Delay(3);
        }

        return steps;
    }

    // ------------------------------------------------------------------
    // 復帰
    // ------------------------------------------------------------------

    [Fact]
    public async Task 復帰したら少し待って同期を1回起こす()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();
        await Task.Delay(30);

        // 間隔（15分）の途中で眠って、起きた
        clock.Advance(TimeSpan.FromMinutes(3));
        sync.SyncSoon();

        var waited = await AdvanceUntilAsync(clock, () => runs >= 1, maxSeconds: 120);

        Assert.Equal(1, runs);

        // 間隔（残り12分）を待たない。かといって、起きた直後に叩きもしない（ネットワークが戻るまで待つ）
        Assert.InRange(waited, (int)BackgroundSync.ResumeDelay.TotalSeconds, (int)BackgroundSync.ResumeDelay.TotalSeconds + 10);
    }

    [Fact]
    public async Task 復帰して同期したあとは通常の間隔に戻る()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();
        await Task.Delay(30);

        sync.SyncSoon();
        await AdvanceUntilAsync(clock, () => runs >= 1, maxSeconds: 120);
        Assert.Equal(1, runs);

        // 次は通常の15分後。それまでには走らない
        await Task.Delay(30);
        clock.Advance(Interval - TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.Equal(1, runs);

        await AdvanceUntilAsync(clock, () => runs >= 2, maxSeconds: 60);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task 失敗で延びた間隔も復帰で待たずに済む()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;
        var succeed = false;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(succeed); }, Interval, clock);
        sync.Start();

        // 繋がらず、間隔が延びた
        clock.Advance(Interval);
        await WaitUntilAsync(() => runs >= 1);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);
        Assert.True(sync.CurrentDelay > Interval);

        // 眠って、起きた。延びた間隔（30分）を待たずに、少し待って1回走る
        succeed = true;
        await Task.Delay(30);
        sync.SyncSoon();

        await AdvanceUntilAsync(clock, () => runs >= 2, maxSeconds: 120);

        Assert.Equal(2, runs);
        await WaitUntilAsync(() => sync.CurrentDelay == Interval);
        Assert.Equal(Interval, sync.CurrentDelay);
    }

    [Fact]
    public async Task 止めてあれば復帰しても起こさない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();
        sync.Stop();

        sync.SyncSoon();
        clock.Advance(Interval);
        await Task.Delay(50);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task 始める前の復帰は何も起こさない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);

        sync.SyncSoon();
        clock.Advance(Interval);
        await Task.Delay(50);

        Assert.Equal(0, runs);
        Assert.False(sync.IsRunning);
    }

    // ------------------------------------------------------------------
    // 手で同期が通ったら、失敗の数えを戻す
    // ------------------------------------------------------------------

    [Fact]
    public async Task 手で同期が通ったら延びた間隔が戻る()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(false); }, Interval, clock);
        sync.Start();

        clock.Advance(Interval);
        await WaitUntilAsync(() => runs >= 1);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);
        Assert.True(sync.CurrentDelay > Interval);

        // 利用者が「今すぐ同期」を押して、通った
        sync.ReportSuccess();

        Assert.Equal(Interval, sync.CurrentDelay);
    }

    [Fact]
    public async Task 手で通ったあとは通常の間隔で待ち直す()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(false); }, Interval, clock);
        sync.Start();

        clock.Advance(Interval);
        await WaitUntilAsync(() => runs >= 1);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);

        // 延びた間隔（30分）を待っているところへ、手で通った。通常の15分で待ち直す
        await Task.Delay(30);
        sync.ReportSuccess();
        await Task.Delay(30);

        await AdvanceUntilAsync(clock, () => runs >= 2, maxSeconds: (int)Interval.TotalSeconds + 30);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task 失敗が無ければ待ちを仕切り直さない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();
        await Task.Delay(30);

        // 手で同期するたびに裏の待ちが仕切り直されると、間隔が永久に延びてしまう
        clock.Advance(Interval - TimeSpan.FromSeconds(2));
        sync.ReportSuccess();
        await Task.Delay(30);

        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => runs >= 1);

        Assert.Equal(1, runs);
    }

    // ------------------------------------------------------------------
    // 復帰の判断
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ClockChange.Resume, true)]
    [InlineData(ClockChange.Suspend, false)]
    [InlineData(ClockChange.PowerStatus, false)]
    [InlineData(ClockChange.TimeChanged, false)]
    public void 復帰のときだけ同期を起こす(ClockChange change, bool expected) =>
        Assert.Equal(expected, ClockChangeRules.ShouldSyncAfter(change));

    [Fact]
    public void 復帰の待ちはネットワークが戻るのに足りる数十秒()
    {
        Assert.InRange(BackgroundSync.ResumeDelay.TotalSeconds, 20, 120);
    }
}
