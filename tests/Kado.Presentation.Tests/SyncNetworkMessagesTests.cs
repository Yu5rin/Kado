using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 右上の同期表示が、失敗の原因を言い分けること。接続・切断の待ちを中止できること。
/// 呼びすぎのときに、裏の同期の間隔を延ばす合図を返すこと。
/// </summary>
public class SyncNetworkMessagesTests
{
    private sealed class FakeGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect { get; set; } = true;

        public SyncReport? Report { get; set; } = new() { CreatedLocal = 1 };

        public Exception? ThrowOnSync { get; set; }

        public Exception? ThrowOnConnect { get; set; }

        /// <summary>true なら、渡された取り消しの印が切られるまで戻らない（ブラウザでの認可待ち・取り消しの通信中）。</summary>
        public bool WaitsOnConnect { get; set; }

        public bool WaitsOnDisconnect { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnConnect is { } error) throw error;

            return WaitsOnConnect ? WaitAsync(cancellationToken, then: () => IsConnected = true) : Task.CompletedTask;
        }

        public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (WaitsOnDisconnect)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                finally
                {
                    // 実物と同じく、止められても控えは消す
                    IsConnected = false;
                }
            }

            IsConnected = false;
            return true;
        }

        private static async Task WaitAsync(CancellationToken cancellationToken, Action then)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            then();
        }

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnSync is { } error) throw error;

            return Task.FromResult(Report);
        }

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    private static async Task<string> FailureTextAsync(Exception error)
    {
        var vm = new SyncViewModel(new FakeGoogle { ThrowOnSync = error });

        await vm.SyncAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        return vm.StatusText;
    }

    // ------------------------------------------------------------------
    // 原因ごとの言い分け
    // ------------------------------------------------------------------

    [Fact]
    public async Task 失敗の原因を言い分ける()
    {
        var proxy = await FailureTextAsync(new GoogleApiException(HttpStatusCode.ProxyAuthenticationRequired, "理由なし"));
        var certificate = await FailureTextAsync(new HttpRequestException("x", new AuthenticationException("y")));
        var unreachable = await FailureTextAsync(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));
        var timeout = await FailureTextAsync(new TaskCanceledException("t", new TimeoutException()));
        var forbidden = await FailureTextAsync(new GoogleApiException(HttpStatusCode.Forbidden, "insufficientPermissions"));
        var tooMany = await FailureTextAsync(new GoogleApiException(HttpStatusCode.TooManyRequests, "rateLimitExceeded"));
        var unreadable = await FailureTextAsync(new JsonException("j"));

        Assert.Contains("プロキシの認証が必要です（407）", proxy, StringComparison.Ordinal);
        Assert.Contains("証明書", certificate, StringComparison.Ordinal);
        Assert.Contains("接続できません", unreachable, StringComparison.Ordinal);
        Assert.Contains("時間内に応答がありませんでした", timeout, StringComparison.Ordinal);
        Assert.Contains("拒否されました（403", forbidden, StringComparison.Ordinal);
        Assert.Contains("多すぎます（429）", tooMany, StringComparison.Ordinal);
        Assert.Contains("応答を読み取れませんでした", unreadable, StringComparison.Ordinal);

        // 「ネットワークに繋がりません」にまとめない
        var all = new[] { proxy, certificate, unreachable, timeout, forbidden, tooMany, unreadable };
        Assert.DoesNotContain(all, text => text.Contains("ネットワークに繋がりません", StringComparison.Ordinal));
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public async Task 種類を決められない通信の失敗は従来の言い方()
    {
        var text = await FailureTextAsync(new HttpRequestException("An error occurred while sending the request."));

        Assert.Contains("ネットワークに繋がりません", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 想定外の例外でも型名を出さない()
    {
        var text = await FailureTextAsync(new InvalidOperationException("Sequence contains no elements"));

        Assert.Contains("同期できませんでした", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sequence contains", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 接続の失敗も原因ごとに言い分ける()
    {
        var vm = new SyncViewModel(new FakeGoogle
        {
            IsConnected = false,
            ThrowOnConnect = new HttpRequestException(
                HttpRequestError.ProxyTunnelError, "tunnel", null, HttpStatusCode.ProxyAuthenticationRequired),
        });

        await vm.ConnectAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("プロキシの認証が必要です（407）", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 認可の受け口を開けなかった理由が画面に出る()
    {
        var vm = new SyncViewModel(new FakeGoogle
        {
            IsConnected = false,
            ThrowOnConnect = new OAuthException(
                "ブラウザからの戻りを受け取る準備ができませんでした（権限が足りません（エラー 5））。確かめてください。"),
        });

        await vm.ConnectAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("準備ができませんでした", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("エラー 5", vm.StatusText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 呼びすぎのときは間隔を延ばす
    // ------------------------------------------------------------------

    [Fact]
    public async Task 呼びすぎで一部を次回に回したら裏の同期は成功と数えない()
    {
        var google = new FakeGoogle
        {
            Report = new SyncReport
            {
                Deferred = true,
                Throttled = true,
                Warnings = [SyncReport.BusyWarning],
            },
        };
        var vm = new SyncViewModel(google);
        var succeeded = 0;
        vm.Succeeded += (_, _) => succeeded++;

        var ok = await vm.SyncQuietlyAsync();

        // 失敗ではないので赤くしない。ただし間隔は延ばす（false を返す）
        Assert.False(ok);
        Assert.Equal(SyncState.Warned, vm.State);
        Assert.Contains("混み合っていたので", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, succeeded);
    }

    [Fact]
    public async Task 一部の不調だけなら間隔は延ばさない()
    {
        var google = new FakeGoogle
        {
            Report = new SyncReport { Deferred = true, Warnings = [SyncReport.BusyWarning] },
        };
        var vm = new SyncViewModel(google);

        Assert.True(await vm.SyncQuietlyAsync());
    }

    [Fact]
    public async Task 呼びすぎの記憶は次の同期に持ち越さない()
    {
        var google = new FakeGoogle { Report = new SyncReport { Throttled = true, Deferred = true } };
        var vm = new SyncViewModel(google);

        Assert.False(await vm.SyncQuietlyAsync());

        google.Report = new SyncReport { CreatedLocal = 1 };
        Assert.True(await vm.SyncQuietlyAsync());
    }

    [Fact]
    public async Task すでに走っていて見送ったときも前回の呼びすぎを引きずらない()
    {
        var google = new FakeGoogle { Report = new SyncReport { Throttled = true, Deferred = true } };
        var vm = new SyncViewModel(google);
        Assert.False(await vm.SyncQuietlyAsync());

        // 次は「すでに走っている」（null）
        google.Report = null;

        Assert.True(await vm.SyncQuietlyAsync());
    }

    [Fact]
    public async Task 同期がうまくいったら成功の合図を出す()
    {
        var vm = new SyncViewModel(new FakeGoogle());
        var succeeded = 0;
        vm.Succeeded += (_, _) => succeeded++;

        await vm.SyncAsync();

        Assert.Equal(1, succeeded);
    }

    [Fact]
    public async Task 失敗のときは成功の合図を出さない()
    {
        var vm = new SyncViewModel(new FakeGoogle { ThrowOnSync = new HttpRequestException("x") });
        var succeeded = 0;
        vm.Succeeded += (_, _) => succeeded++;

        await vm.SyncAsync();

        Assert.Equal(0, succeeded);
    }

    // ------------------------------------------------------------------
    // 接続の待ちを中止できる
    // ------------------------------------------------------------------

    [Fact]
    public async Task 認可の待ちを中止ボタンで止められる()
    {
        var google = new FakeGoogle { IsConnected = false, WaitsOnConnect = true };
        var vm = new SyncViewModel(google);
        var cancelled = 0;
        vm.ConnectCancelled += (_, _) => cancelled++;

        var connecting = vm.ConnectAsync();

        // 待っているあいだ（最長5分）も、中止ボタンが押せる
        Assert.True(vm.IsBusy);
        Assert.Equal("接続中…", vm.StatusText);
        Assert.Equal("中止", vm.ActionLabel);
        Assert.True(vm.CancelSyncCommand.CanExecute(null));

        vm.CancelSyncCommand.Execute(null);
        await connecting;

        // 失敗ではなく、未接続のまま戻る
        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal("Google 未接続", vm.StatusText);
        Assert.Equal(1, cancelled);
        Assert.False(vm.IsBusy);

        // もう一度繋げる
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task 外から渡した取り消しも認可の待ちに効く()
    {
        var google = new FakeGoogle { IsConnected = false, WaitsOnConnect = true };
        var vm = new SyncViewModel(google);
        using var cts = new CancellationTokenSource();

        var connecting = vm.ConnectAsync(cts.Token);
        cts.Cancel();
        await connecting;

        // 利用者のボタンではない取り消し（アプリの終了など）は、時間切れ扱い
        Assert.Equal(SyncState.Failed, vm.State);
    }

    [Fact]
    public async Task 中止したあとの接続の失敗を取り違えない()
    {
        var google = new FakeGoogle { IsConnected = false, WaitsOnConnect = true };
        var vm = new SyncViewModel(google);

        var first = vm.ConnectAsync();
        vm.CancelSyncCommand.Execute(null);
        await first;

        google.WaitsOnConnect = false;
        google.ThrowOnConnect = new HttpRequestException("offline");
        await vm.ConnectAsync();

        // 前回の中止が残って、今回の失敗を「中止」と取り違えない
        Assert.Equal(SyncState.Failed, vm.State);
    }

    [Fact]
    public async Task 切断の通信も中止で諦められ接続は切れる()
    {
        var google = new FakeGoogle { WaitsOnDisconnect = true };
        var vm = new SyncViewModel(google);
        var notDelivered = 0;
        vm.RevocationNotDelivered += (_, _) => notDelivered++;

        var disconnecting = vm.DisconnectAsync();

        Assert.True(vm.IsBusy);
        Assert.Equal("切断中…", vm.StatusText);
        Assert.False(vm.SyncNowCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.CancelSyncCommand.CanExecute(null));

        vm.CancelSyncCommand.Execute(null);
        await disconnecting;

        // 取り消しの通信は諦めたが、こちらの接続は切れている。Google 側に許可が残る案内を出す
        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal(1, notDelivered);
    }
}
