using SlideinaCalendar.Google.Sync;
using SlideinaCalendar.Presentation.Sync;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 同期の中止ボタン（項目8）を押したとき、下のステータス行に断りが出ることを確かめる。
/// <para>中止そのものの筋道は <see cref="SyncViewModelTests"/> 側で確かめている。ここは配線だけ。</para>
/// </summary>
public class MainViewModelSyncCancelTests
{
    /// <summary>中止ボタンが切るまで戻らない、繋ぎ役の代わり。</summary>
    private sealed class FakeGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect { get; set; } = true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new SyncReport();
        }
    }

    [Fact]
    public async Task 中止するとステータス行に出る()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), google: new FakeGoogle());

        var running = main.Sync.SyncAsync();
        main.Sync.CancelSyncCommand.Execute(null);
        await running;

        Assert.Equal("同期を中止しました", main.StatusMessage);
    }
}
