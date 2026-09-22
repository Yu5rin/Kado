using Kado.App.Shell;

namespace Kado.Shell.Tests;

/// <summary>
/// <see cref="PopupActivityTracker"/> ―― メニュー・ポップアップの「開いている数」の
/// 数え方だけを検査する（項目3）。
/// <para>
/// 実際に ContextMenu／Popup を開閉する配線（<c>PopupActivityHooks</c>）は WPF に
/// 依存するのでここでは検査できない。ここで確かめるのは、開いた・閉じたの通知から
/// <see cref="PopupActivityTracker.IsAnyOpen"/> が一意に決まることだけ。
/// </para>
/// </summary>
public class PopupActivityTrackerTests
{
    [Fact]
    public void 何も開いていなければfalse()
    {
        var tracker = new PopupActivityTracker();

        Assert.False(tracker.IsAnyOpen);
        Assert.Equal(0, tracker.OpenCount);
    }

    [Fact]
    public void ひとつ開けばtrueになる()
    {
        var tracker = new PopupActivityTracker();

        tracker.Opened();

        Assert.True(tracker.IsAnyOpen);
        Assert.Equal(1, tracker.OpenCount);
    }

    [Fact]
    public void 開いた数だけ閉じればfalseに戻る()
    {
        var tracker = new PopupActivityTracker();

        // メニューの中にサブメニュー（右クリックメニューの入れ子）が開くなど、
        // 複数が同時に開くことがある
        tracker.Opened();
        tracker.Opened();
        Assert.True(tracker.IsAnyOpen);

        tracker.Closed();
        Assert.True(tracker.IsAnyOpen); // まだ1つ開いたまま

        tracker.Closed();
        Assert.False(tracker.IsAnyOpen);
    }

    [Fact]
    public void 閉じた通知が多く来てもマイナスへ落ちない()
    {
        var tracker = new PopupActivityTracker();

        // 開いた通知を取りこぼした場合の逆側。ここで0未満に落ちると、
        // 次に本当に1つ開いたときに「差し引き0」で IsAnyOpen が false のまま
        // になってしまい、そのメニューが開いているのに引っ込めてしまう
        tracker.Closed();
        tracker.Closed();

        Assert.False(tracker.IsAnyOpen);
        Assert.Equal(0, tracker.OpenCount);

        tracker.Opened();

        Assert.True(tracker.IsAnyOpen);
        Assert.Equal(1, tracker.OpenCount);
    }
}
