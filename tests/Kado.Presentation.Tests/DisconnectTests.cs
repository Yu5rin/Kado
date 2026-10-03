using System.Net;
using Dapper;
using Kado.Google.OAuth;
using Kado.Presentation.Sync;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 「切断」。取り消しの通信が失敗しても、こちらの控え（トークン）と同期の状態は必ず消す。
/// <para>
/// 消えないと、画面は「未接続」なのに控えが残り、裏の同期が勝手に再開する。
/// </para>
/// </summary>
public class DisconnectTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("kado-disconnect-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw error;
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private GoogleClientSecretsStore OwnClientFile()
    {
        var path = Path.Combine(_folder, "google-client.json");

        File.WriteAllText(path, """
            {"installed":{"client_id":"mine.apps.googleusercontent.com","client_secret":"s",
             "auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token"}}
            """);

        return new GoogleClientSecretsStore(path);
    }

    private static InMemoryTokenStore Connected() => new(new OAuthTokens
    {
        AccessToken = "at",
        RefreshToken = "rt",
        ExpiresAt = DateTimeOffset.Now.AddHours(1),
    });

    // ------------------------------------------------------------------
    // GoogleConnection
    // ------------------------------------------------------------------

    [Fact]
    public async Task 通信に失敗しても控えと同期の印を消す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Settings.SetSyncState("calendar:syncToken", "abc");

        var tokens = Connected();
        using var connection = new GoogleConnection(
            test.Workspace, OwnClientFile(), tokens, _ => { },
            new HttpClient(new ThrowingHandler(new HttpRequestException("offline"))));

        var revoked = await connection.DisconnectAsync();

        Assert.False(revoked);
        Assert.Null(tokens.Load());
        Assert.False(connection.IsConnected);
        Assert.Null(test.Workspace.Settings.GetSyncState("calendar:syncToken"));
    }

    [Fact]
    public async Task 通信できれば取り消しが届いたと返す()
    {
        using var test = TestWorkspace.Create();
        var tokens = Connected();
        using var connection = new GoogleConnection(
            test.Workspace, OwnClientFile(), tokens, _ => { }, new HttpClient(new OkHandler()));

        Assert.True(await connection.DisconnectAsync());
        Assert.Null(tokens.Load());
    }

    [Fact]
    public async Task 止められても控えと同期の印は消す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Settings.SetSyncState("tasks:syncToken", "abc");

        var tokens = Connected();
        using var connection = new GoogleConnection(
            test.Workspace, OwnClientFile(), tokens, _ => { }, new HttpClient(new OkHandler()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.DisconnectAsync(cts.Token));

        Assert.Null(tokens.Load());
        Assert.Null(test.Workspace.Settings.GetSyncState("tasks:syncToken"));
    }

    // ------------------------------------------------------------------
    // SyncViewModel
    // ------------------------------------------------------------------

    private sealed class Google : IGoogleSync
    {
        public bool IsConnected { get; set; } = true;

        public bool CanConnect => true;

        public bool Revoked { get; set; } = true;

        public Exception? ThrowOnDisconnect { get; set; }

        /// <summary>投げる前に控えを消すか（実物は finally で消す）。</summary>
        public bool ClearsBeforeThrowing { get; set; } = true;

        public int SyncCount { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnDisconnect is { } error)
            {
                if (ClearsBeforeThrowing) IsConnected = false;
                throw error;
            }

            IsConnected = false;
            return Task.FromResult(Revoked);
        }

        public Task<Kado.Google.Sync.SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncCount++;
            return Task.FromResult<Kado.Google.Sync.SyncReport?>(null);
        }

        public bool HasDriveAttachmentScope => true;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Kado.Google.Sync.GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    [Fact]
    public async Task 取り消しが届かなければ案内を出し接続は切れたことにする()
    {
        var google = new Google { Revoked = false };
        var vm = new SyncViewModel(google);
        var noticed = 0;
        vm.RevocationNotDelivered += (_, _) => noticed++;

        await vm.DisconnectAsync();

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal(1, noticed);
        Assert.Contains("Google アカウントの設定", SyncViewModel.RevocationNotDeliveredMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取り消しが届けば案内は出さない()
    {
        var vm = new SyncViewModel(new Google { Revoked = true });
        var noticed = 0;
        vm.RevocationNotDelivered += (_, _) => noticed++;

        await vm.DisconnectAsync();

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal(0, noticed);
    }

    [Fact]
    public async Task 控えを消したあとで例外が出ても切れたことにする()
    {
        var google = new Google { ThrowOnDisconnect = new HttpRequestException("offline") };
        var vm = new SyncViewModel(google);
        var noticed = 0;
        vm.RevocationNotDelivered += (_, _) => noticed++;

        await vm.DisconnectAsync();

        Assert.Equal(SyncState.Disconnected, vm.State);
        Assert.Equal(1, noticed);
    }

    [Fact]
    public async Task 控えを消せなかったときは切れたことにせず裏の同期も再開させない()
    {
        // 控えが残っているのに「未接続」と見せると、裏の同期が IsConnected を見て走り出す
        var google = new Google
        {
            ThrowOnDisconnect = new IOException("locked"),
            ClearsBeforeThrowing = false,
        };
        var vm = new SyncViewModel(google);

        await vm.DisconnectAsync();

        Assert.Equal(SyncState.Failed, vm.State);
        Assert.Contains("切断できませんでした", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public async Task 切ったあとは裏の同期が走らない()
    {
        var google = new Google { ThrowOnDisconnect = new HttpRequestException("offline") };
        var vm = new SyncViewModel(google);

        await vm.DisconnectAsync();
        var ok = await vm.SyncQuietlyAsync();

        Assert.True(ok);
        Assert.Equal(0, google.SyncCount);
    }
}
