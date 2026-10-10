using Kado.App.Shell;

namespace Kado.Shell.Tests;

/// <summary>操作の無い時間の計算。時刻は渡すだけなので、実時間を待たない。</summary>
public class UserIdleTests
{
    [Fact]
    public void 最後の操作からの経過を出す()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), UserIdle.Between(1_000_000 + 600_000, 1_000_000));
        Assert.Equal(TimeSpan.Zero, UserIdle.Between(5, 5));
    }

    [Fact]
    public void 約49日で一周した時刻をまたいでも合う()
    {
        // GetTickCount は32ビットで一周する。最後の操作は一周の直前、いまは一周の直後
        var last = uint.MaxValue - 59_999;
        var now = 540_000u;

        Assert.Equal(TimeSpan.FromMilliseconds(600_000), UserIdle.Between(now, last));
    }
}
