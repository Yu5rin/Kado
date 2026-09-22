using Kado.Data;
using Kado.Data.Repositories;
using Kado.Presentation.Settings;

namespace Kado.Presentation.Tests;

/// <summary>
/// ウィンドウの置き場所。
/// <para>
/// 閉じたときの位置・大きさ・最大化を覚えて、次の起動で同じように出す。
/// </para>
/// </summary>
public class WindowPlacementTests
{
    private static WindowPlacementStore Store(out Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        return new WindowPlacementStore(new SettingsRepository(connection));
    }

    [Fact]
    public void 何も覚えていなければ既定の大きさで置き場所は持たない()
    {
        var placement = WindowPlacement.Unknown;

        Assert.False(placement.HasPosition);
        Assert.False(placement.IsMaximized);
        Assert.Equal(WindowPlacement.DefaultWidth, placement.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, placement.Height);
    }

    [Fact]
    public void 控えた置き場所をそのまま読み戻す()
    {
        var store = Store(out var connection);
        using var _ = connection;

        store.Save(new WindowPlacement(120, 80, 1400, 900, IsMaximized: false));

        var read = store.Load();

        Assert.Equal(120, read.Left);
        Assert.Equal(80, read.Top);
        Assert.Equal(1400, read.Width);
        Assert.Equal(900, read.Height);
        Assert.False(read.IsMaximized);
    }

    [Fact]
    public void 最大化で終わったら最大化として読み戻す()
    {
        var store = Store(out var connection);
        using var _ = connection;

        // 最大化していても、元に戻したときの大きさを一緒に覚えておく。
        // 覚えないと、元に戻したときに既定の大きさへ落ちる
        store.Save(new WindowPlacement(120, 80, 1400, 900, IsMaximized: true));

        var read = store.Load();

        Assert.True(read.IsMaximized);
        Assert.Equal(1400, read.Width);
        Assert.Equal(900, read.Height);
    }

    [Fact]
    public void 一度も控えていなければ既定を返す()
    {
        var store = Store(out var connection);
        using var _ = connection;

        var read = store.Load();

        Assert.False(read.HasPosition);
        Assert.Equal(WindowPlacement.DefaultWidth, read.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, read.Height);
    }

    [Fact]
    public void 壊れた値は既定に戻す()
    {
        var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
        using var _ = connection;

        var settings = new SettingsRepository(connection);
        settings.Set("window.width", "とても大きい");
        settings.Set("window.height", "");

        var read = new WindowPlacementStore(settings).Load();

        Assert.Equal(WindowPlacement.DefaultWidth, read.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, read.Height);
    }

    [Fact]
    public void 小さすぎる大きさは下限まで戻す()
    {
        var store = Store(out var connection);
        using var _ = connection;

        // 下限を割った大きさで出すと、中身が重なって読めない
        store.Save(new WindowPlacement(0, 0, 100, 100, IsMaximized: false));

        var read = store.Load();

        // 既定まで戻すと、細くして終えた次の起動でいきなり大きく開く。
        // 下限に留めるほうが、そのまま使い続けられる
        Assert.Equal(WindowPlacement.MinWidth, read.Width);
        Assert.Equal(WindowPlacement.MinHeight, read.Height);
    }

    // ------------------------------------------------------------------
    // 画面の中に収める。
    // 外付けのディスプレイを外したまま起動すると、画面の外に開いて手が出せなくなる
    // ------------------------------------------------------------------

    [Fact]
    public void 画面の中にあればそのまま()
    {
        var placement = new WindowPlacement(100, 50, 1180, 760, false)
            .ClampTo(0, 0, 1920, 1080);

        Assert.Equal(100, placement.Left);
        Assert.Equal(50, placement.Top);
        Assert.Equal(1180, placement.Width);
        Assert.Equal(760, placement.Height);
    }

    [Fact]
    public void 画面の外へ出ていたら押し戻す()
    {
        // 右の外付けディスプレイ（1920 から先）に置いたまま、それを外した状態
        var placement = new WindowPlacement(2400, 300, 1180, 760, false)
            .ClampTo(0, 0, 1920, 1080);

        Assert.Equal(1920 - 1180, placement.Left);
        Assert.Equal(300, placement.Top);
    }

    [Fact]
    public void 画面より大きければ縮める()
    {
        var placement = new WindowPlacement(0, 0, 3000, 1800, false)
            .ClampTo(0, 0, 1366, 768);

        Assert.Equal(1366, placement.Width);
        Assert.Equal(768, placement.Height);
    }

    [Fact]
    public void 画面が下限より狭くても下限は割らない()
    {
        var placement = new WindowPlacement(0, 0, 1180, 760, false)
            .ClampTo(0, 0, 640, 400);

        // 幅は画面に収まる。下限（280）を上回っているので、そのまま
        Assert.Equal(640, placement.Width);

        // 高さは画面（400）より下限（520）のほうが大きい。中身が重なって
        // 読めなくなるほうが困るので、はみ出させる
        Assert.Equal(WindowPlacement.MinHeight, placement.Height);

        Assert.Equal(0, placement.Left);
        Assert.Equal(0, placement.Top);
    }

    [Fact]
    public void 左や上にある画面も画面の内側として扱う()
    {
        // 主モニタの左に1枚足すと、座標が負になる
        var placement = new WindowPlacement(-1700, -200, 1180, 760, false)
            .ClampTo(-1920, -300, 3840, 1380);

        Assert.Equal(-1700, placement.Left);
        Assert.Equal(-200, placement.Top);
    }

    [Fact]
    public void 置き場所を覚えていなければ大きさだけ整える()
    {
        var placement = WindowPlacement.Unknown.ClampTo(0, 0, 1920, 1080);

        Assert.False(placement.HasPosition);
        Assert.Equal(WindowPlacement.DefaultWidth, placement.Width);
    }

    [Fact]
    public void 画面の大きさが分からなければ何もしない()
    {
        var original = new WindowPlacement(100, 50, 1180, 760, false);

        Assert.Equal(original, original.ClampTo(0, 0, 0, 0));
    }
}
