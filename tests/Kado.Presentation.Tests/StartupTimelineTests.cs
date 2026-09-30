using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.Tests;

/// <summary>
/// 起動の記録（<see cref="StartupTimeline"/>）。
/// <para>
/// 実機で起動に約14秒かかった件を、次はログだけで切り分けるための仕掛け。
/// 記録そのものが動作を変えたり、起動を止めたりしないことも確かめる。
/// </para>
/// </summary>
public class StartupTimelineTests
{
    /// <summary>進めた分だけ経つ時計。</summary>
    private sealed class FakeClock
    {
        public double Now { get; set; }
    }

    private static (StartupTimeline Timeline, FakeClock Clock, List<string> Lines) Create()
    {
        var clock = new FakeClock();
        var lines = new List<string>();
        var timeline = new StartupTimeline(() => clock.Now) { Sink = lines.Add };

        return (timeline, clock, lines);
    }

    [Fact]
    public void 印を1行にまとめて累計と区間を出す()
    {
        var (timeline, clock, lines) = Create();

        clock.Now = 1185;
        timeline.Mark("OnStartup");
        clock.Now = 2025;
        timeline.Mark("画面作成");
        clock.Now = 14025;
        timeline.Mark("Show終了");
        clock.Now = 14091;

        var line = timeline.Finish();

        Assert.Equal(
            "startup-timing OnStartup=1185ms(+1185ms) →画面作成=2025ms(+840ms) " +
            "→Show終了=14025ms(+12000ms) →ContentRendered=14091ms(+66ms)",
            line);
        Assert.Equal([line!], lines);
    }

    [Fact]
    public void 終えたあとは印も測定も記録しない()
    {
        var (timeline, clock, lines) = Create();

        timeline.Finish();
        lines.Clear();

        timeline.Mark("あと");
        var again = timeline.Finish();

        timeline.Measure("あとの遅い処理", () => clock.Now += 500);

        Assert.Null(again);
        Assert.Empty(lines);
        Assert.True(timeline.IsFinished);
    }

    [Fact]
    public void 百ミリ秒以上かかった処理だけを1行ずつ残す()
    {
        var (timeline, clock, lines) = Create();

        timeline.Measure("速い", () => clock.Now += 99);
        timeline.Measure("ちょうど", () => clock.Now += 100);
        timeline.Measure("遅い", () => clock.Now += 12000);

        Assert.Equal(["startup-slow ちょうど=100ms", "startup-slow 遅い=12000ms"], lines);
    }

    [Fact]
    public void 値を返す処理も測って結果をそのまま返す()
    {
        var (timeline, clock, lines) = Create();

        var result = timeline.Measure("MonthViewModel", () =>
        {
            clock.Now += 250;
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(["startup-slow MonthViewModel=250ms"], lines);
    }

    [Fact]
    public void 処理が例外を投げても時間を残して投げ直す()
    {
        var (timeline, clock, lines) = Create();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            timeline.Measure("落ちる", () =>
            {
                clock.Now += 300;
                throw new InvalidOperationException("失敗");
            }));

        Assert.Equal("失敗", thrown.Message);
        Assert.Equal(["startup-slow 落ちる=300ms"], lines);
    }

    [Fact]
    public void 書き先が無くても書き先が落ちても起動は止まらない()
    {
        var clock = new FakeClock();
        var timeline = new StartupTimeline(() => clock.Now);

        // 書き先が無い
        timeline.Measure("遅い", () => clock.Now += 500);

        // 書き先が例外を投げる
        timeline.Sink = _ => throw new IOException("書けない");
        timeline.Measure("遅い", () => clock.Now += 500);

        Assert.NotNull(timeline.Finish());
    }

    [Fact]
    public void usingで包んだ区間も同じ基準で測る()
    {
        var (timeline, clock, lines) = Create();

        using (timeline.Measure("速い区間")) clock.Now += 10;
        using (timeline.Measure("遅い区間")) clock.Now += 700;

        Assert.Equal(["startup-slow 遅い区間=700ms"], lines);
    }
}
