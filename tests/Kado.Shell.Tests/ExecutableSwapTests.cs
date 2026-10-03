using System.ComponentModel;
using Kado.App.Update;

namespace Kado.Shell.Tests;

/// <summary>
/// 更新の入れ替え。新しい exe を起動できなかったときは、元の版へ巻き戻す。
/// <para>
/// AppLocker・ウイルス対策・SmartScreen に止められると、起動が <see cref="Win32Exception"/> で失敗する。
/// そのまま終わると、次から起動できない exe だけが残る。
/// </para>
/// </summary>
public class ExecutableSwapTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kado-swap-").FullName;

    private string Current => Path.Combine(_folder, "Kado.exe");

    private string Downloaded => Path.Combine(_folder, "Kado-v1.2.3.exe");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void Prepare()
    {
        File.WriteAllText(Current, "旧い版");
        File.WriteAllText(Downloaded, "新しい版");
    }

    [Fact]
    public void 入れ替えて起動できたら成功()
    {
        Prepare();
        string? launched = null;

        var result = ExecutableSwap.Run(Current, Downloaded, exe => launched = exe);

        Assert.True(result.Succeeded);
        Assert.Equal("新しい版", File.ReadAllText(Current));
        Assert.Equal("旧い版", File.ReadAllText(Current + ".old"));
        Assert.Equal(Current, launched);

        // 落としたものは片付く
        Assert.False(File.Exists(Downloaded));
    }

    [Fact]
    public void 起動を止められたら元の版へ戻す()
    {
        Prepare();

        var result = ExecutableSwap.Run(Current, Downloaded, _ => throw new Win32Exception(1260));

        Assert.Equal(SwapOutcome.RolledBack, result.Outcome);
        Assert.IsType<Win32Exception>(result.Error);

        // 元の名前に元の版が戻っている。次の起動で動く
        Assert.Equal("旧い版", File.ReadAllText(Current));
        Assert.False(File.Exists(Current + ".old"));

        // 起動できなかった新しい exe は退けてある（元の名前の場所には無い）
        Assert.Equal("新しい版", File.ReadAllText(Current + ExecutableSwap.FailedSuffix));
    }

    [Fact]
    public void 起動を止めたソフトが新しいexeを隔離していても戻せる()
    {
        Prepare();

        var result = ExecutableSwap.Run(Current, Downloaded, exe =>
        {
            File.Delete(exe);
            throw new Win32Exception(225);
        });

        Assert.Equal(SwapOutcome.RolledBack, result.Outcome);
        Assert.Equal("旧い版", File.ReadAllText(Current));
    }

    [Fact]
    public void 新しいexeを置けなかったときも元の版へ戻す()
    {
        Prepare();

        // 落としたファイルが消えている（コピーに失敗する）
        File.Delete(Downloaded);

        var result = ExecutableSwap.Run(Current, Downloaded, _ => { });

        Assert.Equal(SwapOutcome.RolledBack, result.Outcome);
        Assert.IsAssignableFrom<IOException>(result.Error);
        Assert.Equal("旧い版", File.ReadAllText(Current));
    }

    [Fact]
    public void 名前を変える前に失敗したら何も動かさない()
    {
        // いまの exe が無い（改名に失敗する）
        File.WriteAllText(Downloaded, "新しい版");
        var launched = false;

        var result = ExecutableSwap.Run(Current, Downloaded, _ => launched = true);

        Assert.Equal(SwapOutcome.FailedUnchanged, result.Outcome);
        Assert.False(launched);
        Assert.Equal("新しい版", File.ReadAllText(Downloaded));
    }

    [Fact]
    public void 前回の残りがあっても入れ替えられる()
    {
        Prepare();
        File.WriteAllText(Current + ".old", "さらに前の版");
        File.WriteAllText(Current + ExecutableSwap.FailedSuffix, "前回の失敗");

        var result = ExecutableSwap.Run(Current, Downloaded, _ => { });

        Assert.True(result.Succeeded);
        Assert.Equal("旧い版", File.ReadAllText(Current + ".old"));
        Assert.False(File.Exists(Current + ExecutableSwap.FailedSuffix));
    }

    [Fact]
    public void 失敗の文言は入れ替えの失敗と言いダウンロードの失敗とは言わない()
    {
        Prepare();
        var result = ExecutableSwap.Run(Current, Downloaded, _ => throw new Win32Exception(1260));

        Assert.Contains("起動できませんでした", result.Message, StringComparison.Ordinal);
        Assert.Contains("元の版に戻しました", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ダウンロード", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Exception", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 改名前の失敗の文言は元の版のままと伝える()
    {
        var result = ExecutableSwap.Run(Current, Downloaded, _ => { });

        Assert.Equal(SwapOutcome.FailedUnchanged, result.Outcome);
        Assert.Contains("元の版のままです", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ダウンロード", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 元に戻せなかったときは手で戻す場所を伝える()
    {
        var result = new SwapResult(SwapOutcome.RollbackFailed, new IOException(), @"C:\k\Kado.exe", @"C:\k\Kado.exe.old");

        Assert.Contains(@"C:\k\Kado.exe.old", result.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\k\Kado.exe", result.Message, StringComparison.Ordinal);
    }
}
