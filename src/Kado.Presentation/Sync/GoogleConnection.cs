using Kado.Google.OAuth;
using Kado.Google.Sync;

namespace Kado.Presentation.Sync;

/// <summary>
/// 実物の繋ぎ。OAuth と API をここで組み立てる。
/// <para>
/// クライアント設定は<b>呼ばれるたびに読み直す</b>。設定を取り込んだ直後に、
/// アプリを開き直さずに繋げるようにするため。
/// </para>
/// </summary>
public sealed class GoogleConnection(
    CalendarWorkspace workspace,
    GoogleClientSecretsStore secrets,
    ITokenStore tokens,
    Action<string> openBrowser,
    HttpClient? http = null,
    DateTimeOffset? from = null) : IGoogleSync, IDisposable
{
    /// <summary>Google API への既定の待ち時間。カレンダー数だけ呼ぶので、既定の100秒のままだと長すぎる。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // 外から HttpClient を渡されたときは、そちらの設定を尊重する。自前で作ったときだけ絞る
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = DefaultTimeout };
    private readonly bool _ownsHttp = http is null;

    private GoogleSyncService? _service;
    private GoogleTokenProvider? _provider;

    public bool IsConnected => tokens.Load() is not null;

    public bool CanConnect => secrets.Exists;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Provider().ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Provider().DisconnectAsync(cancellationToken).ConfigureAwait(false);

        // 差分の印も捨てる。繋ぎ直したとき、古い印で呼ぶと 410 になる
        workspace.Settings.ClearSyncState();
    }

    public async Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
    {
        var service = _service ??= new GoogleSyncService(
            workspace,
            new GoogleCalendarApi(_http, Provider()),
            new GoogleTasksApi(_http, Provider()),
            from);

        return await service.SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool HasDriveAttachmentScope =>
        IsConnected && Provider().HasScope(GoogleOAuthOptions.DriveFileScope);

    public async Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
        await Provider().EnsureScopeAsync(GoogleOAuthOptions.DriveFileScope, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// ドライブ API を組み立てる。添付のアップロードにだけ使う。
    /// <para>
    /// 呼ぶ前に <see cref="HasDriveAttachmentScope"/> を確かめること。権限が無いまま
    /// 呼ぶと、Google 側に断られる（403）。
    /// </para>
    /// </summary>
    public GoogleDriveApi CreateDriveApi() => new(_http, Provider());

    /// <summary>トークンを配る係。設定を読み直して組み立てる。</summary>
    private GoogleTokenProvider Provider()
    {
        if (_provider is not null) return _provider;

        var options = secrets.Load()
            ?? throw new OAuthException(
                "Google のクライアント設定がありません。⚙メニューから読み込んでください。");

        return _provider = new GoogleTokenProvider(
            new LoopbackOAuthFlow(options, _http, openBrowser), tokens);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _service?.Dispose();

        if (_ownsHttp) _http.Dispose();
    }
}
