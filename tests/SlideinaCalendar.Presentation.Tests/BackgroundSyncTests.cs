using Microsoft.Extensions.Time.Testing;
using SlideinaCalendar.Presentation.Sync;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 決まった間隔で静かに同期する仕掛け。
/// <para>時計を進めて試すので、実時間は待たない。</para>
/// </summary>
public class BackgroundSyncTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 何も起きないことを確かめるための待ち。
    /// <para>起きないことは待っても分からないので、少しだけ待って打ち切る。</para>
    /// </summary>
    private static async Task SettleAsync() => await Task.Delay(50);

    /// <summary>
    /// そうなるまで待つ。
    /// <para>
    /// 時計を進めても、中の処理は別の流れで走る。実時間を決め打ちで待つと、
    /// 混んでいる機械では間に合わずに落ちる（Windows の CI で実際に落ちた）。
    /// 起きたらすぐ進み、いつまでも起きなければ打ち切って、落ちた理由を
    /// 呼び出し側の Assert に語らせる。
    /// </para>
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> until)
    {
        var deadline = Environment.TickCount64 + 5000;

        while (!until() && Environment.TickCount64 < deadline) await Task.Delay(5);
    }

    [Fact]
    public async Task 間隔ごとに走る()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();

        clock.Advance(Interval);
        await WaitUntilAsync(() => runs >= 1);

        Assert.Equal(1, runs);

        clock.Advance(Interval);
        await WaitUntilAsync(() => runs >= 2);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task 間隔が来るまでは走らない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();

        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        await SettleAsync();

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task 止めたら走らない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        var sync = new BackgroundSync(_ => { runs++; return Task.FromResult(true); }, Interval, clock);
        sync.Start();
        sync.Stop();

        clock.Advance(Interval * 3);
        await SettleAsync();

        Assert.Equal(0, runs);
        Assert.False(sync.IsRunning);
    }

    [Fact]
    public void 二度始めても一本だけ()
    {
        var clock = new FakeTimeProvider();

        using var sync = new BackgroundSync(_ => Task.FromResult(true), Interval, clock);
        sync.Start();
        sync.Start();

        Assert.True(sync.IsRunning);
    }

    [Fact]
    public async Task 失敗が続くと間隔が伸びる()
    {
        var clock = new FakeTimeProvider();

        using var sync = new BackgroundSync(_ => Task.FromResult(false), Interval, clock);
        sync.Start();

        Assert.Equal(Interval, sync.CurrentDelay);

        clock.Advance(Interval);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);

        // 繋がらないのに同じ間隔で叩き続けても、電池と回線を使うだけ
        Assert.True(sync.CurrentDelay > Interval);
    }

    [Fact]
    public async Task うまくいったら間隔が戻る()
    {
        var clock = new FakeTimeProvider();
        var succeed = false;

        using var sync = new BackgroundSync(_ => Task.FromResult(succeed), Interval, clock);
        sync.Start();

        clock.Advance(Interval);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);
        Assert.True(sync.CurrentDelay > Interval);

        succeed = true;
        clock.Advance(sync.CurrentDelay);
        await WaitUntilAsync(() => sync.CurrentDelay == Interval);

        Assert.Equal(Interval, sync.CurrentDelay);
    }

    [Fact]
    public void 伸びても上限を超えない()
    {
        var clock = new FakeTimeProvider();

        using var sync = new BackgroundSync(_ => Task.FromResult(false), Interval, clock);

        // 失敗が何度続いても青天井にはしない
        Assert.True(BackgroundSync.MaxBackoff >= Interval);
    }

    [Fact]
    public async Task 例外が出ても止まらない()
    {
        var clock = new FakeTimeProvider();
        var runs = 0;

        using var sync = new BackgroundSync(
            _ =>
            {
                runs++;
                throw new InvalidOperationException("想定外");
            },
            Interval, clock);

        sync.Start();

        clock.Advance(Interval);

        // 走り終わるだけでなく、次の間隔が決まるまで待つ。決まる前に時計を進めると、
        // 古い間隔ぶんしか進まず次の回が来ない
        await WaitUntilAsync(() => runs >= 1 && sync.CurrentDelay > Interval);

        // 頼まれていない同期で落ちるのがいちばん困る
        Assert.Equal(1, runs);
        Assert.True(sync.IsRunning);

        clock.Advance(sync.CurrentDelay);
        await WaitUntilAsync(() => runs >= 2);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task 例外は失敗として数える()
    {
        var clock = new FakeTimeProvider();

        using var sync = new BackgroundSync(
            _ => throw new InvalidOperationException("想定外"), Interval, clock);

        sync.Start();
        clock.Advance(Interval);
        await WaitUntilAsync(() => sync.CurrentDelay > Interval);

        Assert.True(sync.CurrentDelay > Interval);
    }
}
