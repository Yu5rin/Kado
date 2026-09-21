using System.Net;
using System.Text.Json;
using SlideinaCalendar.Google.OAuth;
using SlideinaCalendar.Google.Sync;
using SlideinaCalendar.Presentation.Sync;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 右上の同期表示と、その操作。
/// <para>実際には繋がず、繋ぎ役を差し替えて画面の振る舞いだけを見る。</para>
/// </summary>
public class SyncViewModelTests
{
    /// <summary>繋ぎ役の代わり。</summary>
    private sealed class FakeGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; }

        public bool CanConnect { get; set; } = true;

        /// <summary>同期が呼ばれた回数。</summary>
        public int SyncCount { get; private set; }

        /// <summary>同期が返す結果。null なら「すでに走っている」。</summary>
        public SyncReport? Report { get; set; } = new() { CreatedLocal = 1 };

        /// <summary>繋ぐときに投げる例外。</summary>
        public Exception? ThrowOnConnect { get; set; }

        /// <summary>同期のときに投げる例外。</summary>
        public Exception? ThrowOnSync { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnConnect is { } error) throw error;

            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnSync is { } error) throw error;

            SyncCount++;
            return Task.FromResult(Report);
        }
    }

    [Fact]
    public void 繋いでいなければそう出す()
    {
        var vm = new SyncViewModel(new FakeGoogle());

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal("Google 未接続", vm.StatusText);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public void 繋ぎ役が無くても落ちない()
    {
        // Google 連携を組み立てていない場面。画面は出せる必要がある
        var vm = new SyncViewModel();

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.SyncNowCommand.CanExecute(null));
    }

    [Fact]
    public void クライアント設定が無ければ繋げない()
    {
        var vm = new SyncViewModel(new FakeGoogle { CanConnect = false });

        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task 繋ぐとそのまま取り込む()
    {
        var google = new FakeGoogle();
        var vm = new SyncViewModel(google);

        await vm.ConnectAsync();

        Assert.Equal(SyncState.Idle, vm.State);
        Assert.True(vm.IsConnected);

        // 繋いだのに何も出ないと、繋がったのか分からない
        Assert.Equal(1, google.SyncCount);
    }

    [Fact]
    public async Task 繋いだら時刻を出す()
    {
        var vm = new SyncViewModel(new FakeGoogle());

        await vm.ConnectAsync();

        Assert.NotNull(vm.LastSyncedAt);
        Assert.StartsWith("同期済み ", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 許可されなければその旨を出す()
    {
        var google = new FakeGoogle { ThrowOnConnect = new OAuthException("access_denied", "access_denied") };
        var vm = new SyncViewModel(google);

        await vm.ConnectAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("許可されませんでした", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 繋がらなければその旨を出す()
    {
        var vm = new SyncViewModel(new FakeGoogle { ThrowOnConnect = new HttpRequestException("dns") });

        await vm.ConnectAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("ネットワーク", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 繋いでいなければ同期しない()
    {
        var google = new FakeGoogle();
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(0, google.SyncCount);
        Assert.Equal(SyncState.Disconnected, vm.State);
    }

    [Fact]
    public async Task 同期が終わると画面に知らせる()
    {
        var vm = new SyncViewModel(new FakeGoogle());
        var notified = 0;
        vm.Synced += (_, _) => notified++;

        await vm.ConnectAsync();

        // 取り込んだ中身が画面に出ないと、同期した意味がない
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task 伝えられなかったことがあれば黙らせない()
    {
        var google = new FakeGoogle
        {
            IsConnected = true,
            Report = new SyncReport { Warnings = ["削除を伝えられませんでした（e1）: backendError"] },
        };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Warned, vm.State);
        Assert.Contains("一部を伝えられません", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 断られたら理由を出す()
    {
        var google = new FakeGoogle
        {
            IsConnected = true,
            ThrowOnSync = new GoogleApiException(HttpStatusCode.Forbidden, "insufficientPermissions"),
        };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("insufficientPermissions", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新トークンが死んでいたら繋ぎ直しを促す()
    {
        var google = new FakeGoogle
        {
            IsConnected = true,
            ThrowOnSync = new OAuthException("invalid_grant", "invalid_grant"),
        };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Contains("繋ぎ直して", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 応答が来ないまま固まらない()
    {
        // GoogleConnection の HttpClient がタイムアウトすると TaskCanceledException が来る。
        // これを拾わずに抜けると State が Running のまま残り、以後の同期が二度と走らなくなる
        var google = new FakeGoogle { IsConnected = true, ThrowOnSync = new TaskCanceledException("timeout") };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.False(vm.IsBusy);
        Assert.Contains("時間内に応答がありませんでした", vm.StatusText, StringComparison.Ordinal);

        // 固まっていないので、もう一度同期できる
        google.ThrowOnSync = null;
        await vm.SyncAsync();
        Assert.Equal(SyncState.Idle, vm.State);
    }

    [Fact]
    public async Task 応答を読み取れなければ固まらない()
    {
        // キャプティブポータルが HTML を 200 で返すと、応答を JSON として読めず JsonException になる
        var google = new FakeGoogle
        {
            IsConnected = true,
            ThrowOnSync = new JsonException("invalid json"),
        };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.False(vm.IsBusy);
        Assert.Contains("応答を読み取れませんでした", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 想定外の例外でも固まらない()
    {
        var google = new FakeGoogle
        {
            IsConnected = true,
            ThrowOnSync = new InvalidOperationException("想定外"),
        };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.False(vm.IsBusy);

        // 固まっていないので、SyncNowCommand / DisconnectCommand が再び押せる
        Assert.True(vm.SyncNowCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task すでに走っていれば何もしない()
    {
        // 同じ予定を2本が書き換えると、どちらが勝ったのか分からなくなる
        var google = new FakeGoogle { IsConnected = true, Report = null };
        var vm = new SyncViewModel(google);

        await vm.SyncAsync();

        Assert.Equal(SyncState.Idle, vm.State);
        Assert.Null(vm.LastSyncedAt);
    }

    [Fact]
    public async Task 切ると未接続に戻る()
    {
        var google = new FakeGoogle();
        var vm = new SyncViewModel(google);
        await vm.ConnectAsync();

        await vm.DisconnectAsync();

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Null(vm.LastSyncedAt);
        Assert.Null(vm.LastReport);
    }

    [Fact]
    public void 設定を入れたら繋げるようになる()
    {
        // クライアント設定を読み込むまでは繋げない
        var google = new FakeGoogle { CanConnect = false };
        var vm = new SyncViewModel(google);

        Assert.False(vm.ConnectCommand.CanExecute(null));

        // 読み込んだ、という想定
        google.CanConnect = true;
        vm.RefreshAvailability();

        // これが通らないと、設定を入れたのに「Google に接続…」が押せないままになる。
        // このコマンドは CommandManager に乗っていないので、自分で知らせないと変わらない
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.CanConnect);
    }

    [Fact]
    public void 押せるかどうかが変わったことを画面に知らせる()
    {
        var google = new FakeGoogle { CanConnect = false };
        var vm = new SyncViewModel(google);

        var changed = 0;
        vm.ConnectCommand.CanExecuteChanged += (_, _) => changed++;

        google.CanConnect = true;
        vm.RefreshAvailability();

        Assert.True(changed > 0);
    }

    [Fact]
    public async Task 繋いだあとだけ同期を押せる()
    {
        var google = new FakeGoogle();
        var vm = new SyncViewModel(google);

        Assert.False(vm.SyncNowCommand.CanExecute(null));

        await vm.ConnectAsync();
        Assert.True(vm.SyncNowCommand.CanExecute(null));

        // すでに繋いであるなら、繋ぐ操作は出さない
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }
}
