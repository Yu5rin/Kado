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

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(report);

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Kado.Google.Sync.GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    /// <summary>
    /// 手元へ予定を書き込んでから、渡された出来事で終わる繋ぎ役の代わり。
    /// 実物の同期は別の接続で書くので、画面は書いたことを自分では知らない。
    /// </summary>
    private sealed class WritingGoogle(
        Action write, Exception? thrown, bool? mayHaveWritten = null, bool waitForCancel = false) : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect { get; set; } = true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public async Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            write();

            // 書いたあと、中止ボタンが押されるまで待つ
            if (waitForCancel) await Task.Delay(Timeout.Infinite, cancellationToken);

            return thrown is null ? new SyncReport() : throw thrown;
        }

        public bool HasDriveAttachmentScope => false;

        // 答えを持たない実装は、既定（分からないので true）のまま
        public bool MayHaveWrittenBeforeInterruption => mayHaveWritten ?? true;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Kado.Google.Sync.GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    /// <summary>
    /// 同期用の別の接続が書いたことの代わり。画面の側の DataChanged を通さずに、表へ直に入れる。
    /// </summary>
    private static void WriteBehindTheScreen(TestWorkspace test) =>
        test.Workspace.Events.Upsert(new Kado.Data.Models.CalendarEvent
        {
            Id = "synced1", Title = "同期で入った予定", Date = new DateOnly(2026, 9, 24),
        });

    [Fact]
    public async Task 失敗しても書き込んでいたかもしれなければ画面を読み直す()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24),
            google: new WritingGoogle(() => WriteBehindTheScreen(test), new HttpRequestException("切れた")));
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        await main.Sync.SyncAsync();

        Assert.Equal(SyncState.Failed, main.Sync.State);
        Assert.True(changed > 0);
        Assert.Contains(main.SelectedDay.Events, _ => true);
    }

    [Fact]
    public async Task 中止しても書き込んでいたかもしれなければ画面を読み直す()
    {
        using var test = TestWorkspace.Create();
        var google = new WritingGoogle(
            () => WriteBehindTheScreen(test), thrown: null, waitForCancel: true);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), google: google);
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        var running = main.Sync.SyncAsync();
        main.Sync.CancelSyncCommand.Execute(null);
        await running;

        Assert.Equal("同期を中止しました", main.StatusMessage);
        Assert.True(changed > 0);
        Assert.Contains(main.SelectedDay.Events, _ => true);
    }

    [Fact]
    public async Task 何も書いていないと分かっている失敗では画面を読み直さない()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24),
            google: new WritingGoogle(() => { }, new HttpRequestException("繋がらない"), mayHaveWritten: false));
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        await main.Sync.SyncAsync();

        Assert.Equal(SyncState.Failed, main.Sync.State);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task 件数に数えられない書き込みがあった報告でも画面を読み直す()
    {
        using var test = TestWorkspace.Create();
        var changed = 0;
        test.Workspace.DataChanged += (_, _) => changed++;

        // 件数は全部0。ただし、1つのカレンダーの同期が途中で転んだ（書いたかもしれない）
        var report = new SyncReport { MayHaveWritten = true, Warnings = ["「仕事」に繋がりません"] };
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24), google: new FakeGoogle(report));

        await main.Sync.SyncAsync();

        Assert.True(changed > 0);
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
