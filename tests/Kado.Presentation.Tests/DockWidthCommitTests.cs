using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 幅をつまんでいるあいだは保存せず、離したときに1回だけ保存する。
/// <para>
/// 幅の記憶は <see cref="ShellViewModel.DockWidthCommitted"/> で受ける。つまむあいだ動かすたびに
/// 来る <see cref="ShellViewModel.DockWidthChanged"/> で DB へ書くと、マウスが動くたびに書き込みが走る。
/// </para>
/// </summary>
public class DockWidthCommitTests
{
    private static ShellViewModel Create() => new(DockPlacement.Unknown with { Mode = ShellMode.Dock, Width = 400 });

    [Fact]
    public void つまんでいるあいだは動かすたびにChangedが来るが_Committedは来ない()
    {
        var vm = Create();
        var changed = new List<double>();
        var committed = new List<double>();
        vm.DockWidthChanged += (_, w) => changed.Add(w);
        vm.DockWidthCommitted += (_, w) => committed.Add(w);

        vm.IsResizing = true;
        for (var w = 410; w <= 600; w += 10) vm.DockWidth = w;

        // 窓を手に追従させるための通知は、動かすたびに来る
        Assert.Equal(20, changed.Count);

        // 保存の合図は来ない
        Assert.Empty(committed);
    }

    [Fact]
    public void 離したときに_決まった幅を1回だけ知らせる()
    {
        var vm = Create();
        var committed = new List<double>();
        vm.DockWidthCommitted += (_, w) => committed.Add(w);

        vm.IsResizing = true;
        vm.DockWidth = 450;
        vm.DockWidth = 520;
        vm.DockWidth = 640;
        vm.IsResizing = false;

        Assert.Equal([640d], committed);
        Assert.Equal(640, vm.Placement().Width);
    }

    [Fact]
    public void 動かさずに離しても_保存の合図は来ない()
    {
        var vm = Create();
        var committed = 0;
        vm.DockWidthCommitted += (_, _) => committed++;

        vm.IsResizing = true;
        vm.IsResizing = false;

        Assert.Equal(0, committed);
    }

    [Fact]
    public void 同じ幅に戻して離しても_動いたのだから1回知らせる()
    {
        var vm = Create();
        var committed = 0;
        vm.DockWidthCommitted += (_, _) => committed++;

        vm.IsResizing = true;
        vm.DockWidth = 500;
        vm.DockWidth = 400;
        vm.IsResizing = false;

        // 途中で動いていれば知らせる（保存は同じ値の上書きで済む）
        Assert.Equal(1, committed);
    }

    [Fact]
    public void つまんでいないときの変更は_その場で決まる()
    {
        var vm = Create();
        var committed = new List<double>();
        vm.DockWidthCommitted += (_, w) => committed.Add(w);

        // 設定画面からの変更など
        vm.DockWidth = 500;
        vm.DockWidth = 500;   // 同じ値は変更ではない
        vm.DockWidth = 520;

        Assert.Equal([500d, 520d], committed);
    }

    [Fact]
    public void 続けて2回つまんでも_それぞれ離したときに1回()
    {
        var vm = Create();
        var committed = new List<double>();
        vm.DockWidthCommitted += (_, w) => committed.Add(w);

        vm.IsResizing = true;
        vm.DockWidth = 450;
        vm.IsResizing = false;

        vm.IsResizing = true;
        vm.DockWidth = 480;
        vm.DockWidth = 500;
        vm.IsResizing = false;

        Assert.Equal([450d, 500d], committed);
    }

    [Fact]
    public void つまみ始めに前回の動きを引きずらない()
    {
        var vm = Create();
        var committed = 0;
        vm.DockWidthCommitted += (_, _) => committed++;

        vm.IsResizing = true;
        vm.DockWidth = 450;

        // 離さないまま、もう一度つまみ直した（印だけ立て直す）
        vm.IsResizing = false;
        Assert.Equal(1, committed);

        vm.IsResizing = true;
        vm.IsResizing = false;
        Assert.Equal(1, committed);
    }
}
