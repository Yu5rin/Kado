using Kado.Google.Sync;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 同期のあとの画面の作り直し（項目7）。
/// <para>
/// <c>MainViewModel</c> は <c>Sync.Synced</c> を受けて <c>EnsureSources</c>・
/// <c>ReloadWorkingDays</c> を呼び、その中から <c>CalendarWorkspace.DataChanged</c> が飛んで
/// 画面が引き直る。ここでは、その <c>DataChanged</c> が飛ぶかどうかで「作り直したか」を見る。
/// </para>
/// </summary>
public class MainViewModelSyncRefreshTests
{
    /// <summary>渡された結果をそのまま返すだけの、繋ぎ役の代わり。</summary>
    private sealed class FakeGoogle(SyncReport? report) : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect { get; set; } = true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(report);

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Kado.Google.Sync.GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    [Fact]
    public async Task 何も変わっていない同期では画面を作り直さない()
    {
        using var test = TestWorkspace.Create();
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24), google: new FakeGoogle(new SyncReport()));

        await main.Sync.SyncAsync();

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task 何か変わった同期では画面を作り直す()
    {
        using var test = TestWorkspace.Create();
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        var report = new SyncReport { CreatedLocal = 1 };
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24), google: new FakeGoogle(report));

        await main.Sync.SyncAsync();

        Assert.True(changed > 0);
    }

    [Fact]
    public async Task カレンダー一覧だけが変わった同期でも画面を作り直す()
    {
        using var test = TestWorkspace.Create();
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        // カレンダーが消えただけ（created/updated/deleted/relinked/moved は0）でも、
        // SourcesChanged で拾えて作り直しが起きる
        var report = new SyncReport { SourcesChanged = true };
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24), google: new FakeGoogle(report));

        await main.Sync.SyncAsync();

        Assert.True(changed > 0);
    }
}
