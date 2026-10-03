using Kado.Google.Sync;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 同期の警告。最初の1件しか出ない・次の同期で消える、では何が伝わらなかったのか分からない。
/// </summary>
public class SyncWarningsTests
{
    private sealed class FakeGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect => true;

        public SyncReport? Report { get; set; } = new();

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    private static SyncReport Warn(params string[] warnings) => new() { Warnings = warnings };

    [Fact]
    public async Task 警告が複数あれば件数と全文を読める()
    {
        var google = new FakeGoogle { Report = Warn("一件目の警告", "二件目の警告", "三件目の警告") };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(3, vm.WarningCount);
        Assert.True(vm.HasUnreadWarnings);
        Assert.Equal(["一件目の警告", "二件目の警告", "三件目の警告"], vm.Warnings);

        // 状態の文には最初の1件と、残りの件数
        Assert.Contains("一件目の警告", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("ほか2件", vm.StatusText, StringComparison.Ordinal);

        // 全文はツールチップ・一覧で読める
        Assert.Contains("二件目の警告", vm.WarningsText, StringComparison.Ordinal);
        Assert.Contains("三件目の警告", vm.DetailText, StringComparison.Ordinal);
        Assert.Contains("3件", vm.WarningHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 次の同期で警告が無くても読んだことにするまで消さない()
    {
        var google = new FakeGoogle { Report = Warn("削除を伝えられませんでした") };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        // 裏の次の同期は無事に終わった
        google.Report = new SyncReport { CreatedLocal = 1 };
        await vm.SyncAsync();

        Assert.Equal(SyncState.Warned, vm.State);
        Assert.Equal(["削除を伝えられませんでした"], vm.Warnings);
        Assert.Contains("一部を伝えられません", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 読んだことにすると消えて落ち着いた状態に戻る()
    {
        var google = new FakeGoogle { Report = Warn("警告A", "警告B") };
        var vm = new SyncViewModel(google);
        await vm.SyncAsync();

        Assert.True(vm.AcknowledgeWarningsCommand.CanExecute(null));
        vm.AcknowledgeWarningsCommand.Execute(null);

        Assert.Empty(vm.Warnings);
        Assert.False(vm.HasUnreadWarnings);
        Assert.Equal(SyncState.Idle, vm.State);
        Assert.False(vm.AcknowledgeWarningsCommand.CanExecute(null));
        Assert.DoesNotContain("警告", vm.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 同じ警告は重ねず新しい警告は足す()
    {
        var google = new FakeGoogle { Report = Warn("同じ警告") };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();
        await vm.SyncAsync();
        Assert.Equal(1, vm.WarningCount);

        google.Report = Warn("同じ警告", "新しい警告");
        await vm.SyncAsync();

        Assert.Equal(["同じ警告", "新しい警告"], vm.Warnings);
    }

    [Fact]
    public async Task 警告が無ければ状態の文だけを出す()
    {
        var google = new FakeGoogle { Report = new SyncReport { CreatedLocal = 1 } };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Idle, vm.State);
        Assert.Equal(vm.StatusText, vm.DetailText);
        Assert.Equal(string.Empty, vm.WarningsText);
    }

    [Fact]
    public async Task 接続を切ると警告も捨てる()
    {
        var google = new FakeGoogle { Report = Warn("警告A") };
        var vm = new SyncViewModel(google);
        await vm.SyncAsync();

        await vm.DisconnectAsync();

        Assert.Empty(vm.Warnings);
        Assert.Equal(SyncState.Disconnected, vm.State);
    }
}
