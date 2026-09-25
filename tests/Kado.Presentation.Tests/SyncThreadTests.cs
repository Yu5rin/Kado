using Kado.Google.Sync;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 同期が終わったあと、どのスレッドで画面を触るか。
/// <para>
/// WPF は、作ったスレッド以外から画面の持ち物を触らせない。同期は通信を挟むので
/// 待ち合わせが入り、戻ってくる先を指定しないと別のスレッドになる。そのまま状態を
/// 書き換えると「このオブジェクトは別のスレッドに所有されているため…」で落ちる。
/// </para>
/// <para>
/// WPF は Windows でしか動かせないので、ここでは<b>呼び出し元のスレッドを覚えておく
/// 仕組み</b>（<see cref="SynchronizationContext"/>）を置いて、戻ってきているかを見る。
/// WPF もこの仕組みで UI スレッドへ戻している。
/// </para>
/// </summary>
public class SyncThreadTests
{
    /// <summary>
    /// 1本の決まったスレッドで実行する。WPF の UI スレッドに相当する。
    /// </summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Work, object? State)>
            _queue = new();

        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (work, state) in _queue.GetConsumingEnumerable()) work(state);
            })
            { IsBackground = true };

            _thread.Start();
        }

        /// <summary>このスレッドの識別子。ここへ戻ってきたかを見る。</summary>
        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        /// <summary>このスレッドで動かし、終わるまで待つ。</summary>
        public Task RunAsync(Func<Task> work)
        {
            var done = new TaskCompletionSource();

            Post(async _ =>
            {
                try
                {
                    await work();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }, null);

            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>通信のつもりで、いったん別のスレッドへ逃げる繋ぎ役。</summary>
    private sealed class ThreadHoppingGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect => true;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.Run(() => Thread.Sleep(10), cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            Task.Run(() => Thread.Sleep(10), cancellationToken);

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
            Task.Run<SyncReport?>(
                () =>
                {
                    // 実際の通信と同じく、呼ばれたスレッドから離れる
                    Thread.Sleep(10);
                    return new SyncReport { CreatedLocal = 1 };
                },
                cancellationToken);

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Kado.Google.Sync.GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    [Fact]
    public async Task 同期のあと呼ばれた側のスレッドへ戻る()
    {
        using var ui = new SingleThreadContext();
        var vm = new SyncViewModel(new ThreadHoppingGoogle());

        int syncedOn = 0;
        vm.Synced += (_, _) => syncedOn = Environment.CurrentManagedThreadId;

        await ui.RunAsync(() => vm.SyncAsync());

        // ここが食い違うと、WPF では「別のスレッドに所有されているため」で落ちる
        Assert.Equal(ui.ThreadId, syncedOn);
    }

    [Fact]
    public async Task 繋いだあとも呼ばれた側のスレッドへ戻る()
    {
        using var ui = new SingleThreadContext();
        var vm = new SyncViewModel(new ThreadHoppingGoogle { IsConnected = false });

        int syncedOn = 0;
        vm.Synced += (_, _) => syncedOn = Environment.CurrentManagedThreadId;

        await ui.RunAsync(() => vm.ConnectAsync());

        Assert.Equal(ui.ThreadId, syncedOn);
    }

    [Fact]
    public async Task 切るときも呼ばれた側のスレッドへ戻る()
    {
        using var ui = new SingleThreadContext();
        var vm = new SyncViewModel(new ThreadHoppingGoogle());

        int syncedOn = 0;
        vm.Synced += (_, _) => syncedOn = Environment.CurrentManagedThreadId;

        await ui.RunAsync(() => vm.DisconnectAsync());

        Assert.Equal(ui.ThreadId, syncedOn);
    }

    [Fact]
    public async Task 裏での同期でも呼ばれた側のスレッドへ戻る()
    {
        // 裏の同期は画面を割り込ませないが、状態は同じように書き換える
        using var ui = new SingleThreadContext();
        var vm = new SyncViewModel(new ThreadHoppingGoogle());

        int syncedOn = 0;
        vm.Synced += (_, _) => syncedOn = Environment.CurrentManagedThreadId;

        await ui.RunAsync(async () => await vm.SyncQuietlyAsync());

        Assert.Equal(ui.ThreadId, syncedOn);
    }
}
