using Kado.App.Shell;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Shell.Tests;

/// <summary>
/// AppBar が外れたときの受け方と、終了時の終了印（<c>workarea.reserved</c>）の片付け。
/// <para>
/// 不具合：ピン留めのまま終了すると、外れた通知が居かたを Overlay に書き換えて保存され、
/// 次の起動でピンが外れてスライドになっていた。実際の AppBar 登録（Win32）は
/// Linux では動かせないので、外れる通知は <c>removeAppBar</c> の中から
/// <see cref="UndockReaction.OnUndocked"/> を呼んで再現する（<c>AppBarHost.Dispose</c>
/// が <c>Undock</c> → <c>Undocked</c> を起こす流れと同じ）。
/// </para>
/// </summary>
public class UndockReactionTests
{
    private static ShellViewModel Pinned() =>
        new(DockPlacement.Unknown with { Mode = ShellMode.Dock });

    [Fact]
    public void 終了処理中のUndockedではモードがDockのまま保存される()
    {
        var shell = Pinned();
        var saved = new List<ShellMode>();
        shell.ModeChanged += (_, mode) => saved.Add(mode);   // App 側はこれで Save() する
        var reaction = new UndockReaction(shell, _ => { });

        reaction.Shutdown(removeAppBar: reaction.OnUndocked);

        Assert.Equal(ShellMode.Dock, shell.Mode);
        Assert.True(shell.IsPinned);
        Assert.Equal(ShellMode.Dock, shell.Placement().Mode);
        Assert.Empty(saved);   // ModeChanged が出ないので Apply(Overlay) も Save もされない
    }

    [Fact]
    public void 終了処理で終了印が消える()
    {
        var shell = Pinned();
        var marks = new List<bool>();
        var reaction = new UndockReaction(shell, marks.Add);

        reaction.Shutdown(removeAppBar: reaction.OnUndocked);

        Assert.Equal([false], marks);
    }

    [Fact]
    public void 終了印はAppBarを外したあとで消す()
    {
        var shell = Pinned();
        var order = new List<string>();
        var reaction = new UndockReaction(shell, _ => order.Add("印を消す"));

        reaction.Shutdown(removeAppBar: () => order.Add("外す"));

        // 逆にすると、外す途中で落ちたときに印が残らない
        Assert.Equal(["外す", "印を消す"], order);
    }

    [Fact]
    public void 終了処理を何度呼んでも安全でモードはDockのまま()
    {
        var shell = Pinned();
        var reaction = new UndockReaction(shell, _ => { });

        reaction.Shutdown(reaction.OnUndocked);
        reaction.Shutdown(() => { });

        Assert.Equal(ShellMode.Dock, shell.Mode);
    }

    [Fact]
    public void 終了処理以外のUndockedでは今までどおりOverlayになる()
    {
        var shell = Pinned();
        var told = new List<ShellMode>();
        shell.ModeChanged += (_, mode) => told.Add(mode);
        var reaction = new UndockReaction(shell, _ => { });

        reaction.OnUndocked();   // 全画面アプリや OS 側の都合で登録が外れた

        Assert.Equal(ShellMode.Overlay, shell.Mode);
        Assert.Equal([ShellMode.Overlay], told);
    }

    [Theory]
    [InlineData(ShellMode.Window)]
    [InlineData(ShellMode.Overlay)]
    public void ピンでないときのUndockedは居かたを変えない(ShellMode mode)
    {
        var shell = new ShellViewModel(DockPlacement.Unknown with { Mode = mode });
        var reaction = new UndockReaction(shell, _ => { });

        reaction.OnUndocked();

        Assert.Equal(mode, shell.Mode);
    }
}

/// <summary>終了印ファイルの置く・消す。異常終了で残す側の意味は変えていない。</summary>
public class WorkAreaMarkerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kado-marker-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "workarea.reserved");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void 削っているあいだは印が置かれ_戻したら消える()
    {
        WorkAreaMarker.Set(Path_, true);
        Assert.True(File.Exists(Path_));

        WorkAreaMarker.Set(Path_, false);
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void 印が無くても消す操作は失敗しない()
    {
        WorkAreaMarker.Set(Path_, false);

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void 終了処理で消した印は異常終了の印と区別できる()
    {
        // 異常終了（プロセスのキルなど）は Shutdown を通らないので、印は置いたまま残る
        var shell = new ShellViewModel(DockPlacement.Unknown with { Mode = ShellMode.Dock });
        WorkAreaMarker.Set(Path_, true);

        Assert.True(File.Exists(Path_));

        // 正常終了は Shutdown が印を消す
        new UndockReaction(shell, reserved => WorkAreaMarker.Set(Path_, reserved))
            .Shutdown(() => { });

        Assert.False(File.Exists(Path_));
    }
}
