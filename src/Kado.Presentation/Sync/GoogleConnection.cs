using Kado.Core.Net;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Net;

namespace Kado.Presentation.Sync;

/// <summary>
/// 実物の繋ぎ。OAuth と API をここで組み立てる。
/// <para>
/// クライアント設定は<b>呼ばれるたびに読み直す</b>。設定を取り込んだ直後に、
/// アプリを開き直さずに繋げるようにするため。
/// </para>
/// <para>
/// <b>通信の始まりは、画面のスレッドから外す（<see cref="Task.Run(Func{Task})"/>）。</b>
/// 最初の HTTP 要求は、経路（プロキシ）の自動検出で呼んだスレッドのまま数秒止まることがあり
/// （会社の回線）、画面のスレッドで始めると固まる。Google の同期・接続・切断・添付の、どの入口も
/// 同じ。トークンの保管（DPAPI）やクライアント設定のファイル読みも、同じ理由でスレッドの外に出る。
/// ここから先は画面用の DB 接続に触らない（同期は専用の接続を持つ <see cref="CalendarWorkspace"/> を使う）。
/// 呼んだ側が画面の接続に触るなら、<c>await</c> のあと呼んだスレッドへ戻ること（<c>ConfigureAwait(true)</c>）。
/// </para>
/// <para>
/// 失敗は shell.log に1行ずつ残す（<see cref="NetworkLog"/>）。
/// </para>
/// </summary>
public sealed class GoogleConnection(
    CalendarWorkspace workspace,
    GoogleClientSecretsStore secrets,
    ITokenStore tokens,
    Action<string> openBrowser,
    HttpClient? http = null,
    DateTimeOffset? from = null,
    NetworkLog? log = null,
    GoogleRetryPolicy? retry = null) : IGoogleSync, IDisposable
{
    /// <summary>Google API への既定の待ち時間。カレンダー数だけ呼ぶので、既定の100秒のままだと長すぎる。</summary>
    private static readonly TimeSpan DefaultTimeout = KadoHttp.DefaultTimeout;

    private const string ConnectArea = "Google 接続";
    private const string SyncArea = "Google 同期";
    private const string DisconnectArea = "Google 切断";
    private const string ProxyArea = "Google 通信";

    // 作り方は共通の工場（KadoHttp）。認証付きプロキシ（407）へ、ログオン中のユーザーの資格情報を渡す設定も
    // そこにある。外から HttpClient を渡されたときは、そちらの設定を尊重する。自前で作ったときだけ絞る
    private readonly HttpClient _http = http ?? KadoHttp.CreateClient(DefaultTimeout);
    private readonly bool _ownsHttp = http is null;

    /// <summary>
    /// 添付のアップロードに使う。全体の待ち時間を<b>設けない</b>（30秒では、大きなファイルを送れない）。
    /// かわりにアップロード自身が、大きさに応じた上限と、進み具合が止まったときの打ち切りを持つ
    /// （<see cref="GoogleDriveApi.UploadFileAsync"/>）。外から渡されたときは、同じものを使う。
    /// </summary>
    private readonly HttpClient _uploadHttp = http ?? KadoHttp.CreateClient(Timeout.InfiniteTimeSpan);

    private readonly NetworkLog _log = log ?? NetworkLog.None;

    /// <summary>429・5xx のときに待って出し直す方針。同期ごとに、待った合計を数え直す。</summary>
    private readonly GoogleRetryPolicy _retry = retry ?? GoogleRetryPolicy.Default;

    /// <summary>遅延して組み立てる部品の、組み立てを1本にする。</summary>
    private readonly object _build = new();

    private GoogleSyncService? _service;
    private GoogleTokenProvider? _provider;

    public bool IsConnected => tokens.Load() is not null;

    public bool CanConnect => secrets.Exists;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        // 認可の待ちは、ブラウザが戻るまで続く。画面のスレッドを止めないよう外で始める。
        // トークン交換の通信（プロキシの自動検出を含む）も、ここで外に出る
        Task.Run(async () =>
        {
            try
            {
                _log.LogProxyOnce(ProxyArea, "https://oauth2.googleapis.com/");

                await Provider().ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(ConnectArea, ex, cancellationToken);
                throw;
            }
        });

    public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => DisconnectCoreAsync(cancellationToken));

    private async Task<bool> DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        var revoked = false;

        try
        {
            _log.LogProxyOnce(ProxyArea, "https://oauth2.googleapis.com/");

            // クライアント設定のファイルが消えていると Provider() が作れず、取り消しを送れない。
            // それでも控えは消せないといけない（下の finally）
            GoogleTokenProvider? provider = null;
            try
            {
                provider = Provider();
            }
            catch (OAuthException)
            {
            }

            revoked = provider is not null
                ? await provider.DisconnectAsync(cancellationToken).ConfigureAwait(false)
                : tokens.Load() is null;

            // 取り消しの通信が転んだことは、例外ではなく戻り値で来る。理由は取り消しの通信を
            // 担った LoopbackOAuthFlow が記録に残している。ここでは結果だけを1行にする
            if (!revoked) _log.Write(DisconnectArea, "Google 側の取り消しは届かなかった（こちらの接続は切った）");
        }
        catch (Exception ex)
        {
            LogFailure(DisconnectArea, ex, cancellationToken);
            throw;
        }
        finally
        {
            // 取り消しの通信が転んでも（あるいは止められても）、控えと同期の印は必ず消す。
            // 控えを残すと繋がったままに見え、裏の同期が再開する
            try
            {
                tokens.Clear();
            }
            finally
            {
                // 差分の印も捨てる。繋ぎ直したとき、古い印で呼ぶと 410 になる
                workspace.Settings.ClearSyncState();
            }
        }

        return revoked;
    }

    public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default) =>
        // 同期は最初の API 呼び出しだけでなく、同期用の DB（画面とは別の接続）の読み書きも
        // 画面のスレッドから外して行う。同期用の接続は、この同期だけが使う
        Task.Run(async () =>
        {
            try
            {
                _log.LogProxyOnce(ProxyArea, "https://www.googleapis.com/");

                // 同期ごとに、出し直しで待った合計を数え直す
                _retry.ResetBudget();

                return await Service().SyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(SyncArea, ex, cancellationToken);
                throw;
            }
        });

    public bool HasDriveAttachmentScope =>
        IsConnected && Provider().HasScope(GoogleOAuthOptions.DriveFileScope);

    public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            try
            {
                _log.LogProxyOnce(ProxyArea, "https://oauth2.googleapis.com/");

                return await Provider().EnsureScopeAsync(GoogleOAuthOptions.DriveFileScope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(ConnectArea, ex, cancellationToken, "ドライブの権限の追加");
                throw;
            }
        });

    /// <summary>
    /// ドライブ API を組み立てる。添付のアップロードにだけ使う。
    /// <para>
    /// 呼ぶ前に <see cref="HasDriveAttachmentScope"/> を確かめること。権限が無いまま
    /// 呼ぶと、Google 側に断られる（403）。
    /// </para>
    /// <para>
    /// 通信は呼んだ側が始める。呼び出しは画面のスレッドから来るので、<b>呼んだ側が
    /// <c>Task.Run</c> の中で使うこと</b>（<see cref="GoogleDriveAttachmentUploader"/>）。
    /// </para>
    /// </summary>
    public GoogleDriveApi CreateDriveApi() => new(_uploadHttp, Provider(), _retry);

    /// <summary>トークンを配る係。設定を読み直して組み立てる。</summary>
    private GoogleTokenProvider Provider()
    {
        lock (_build)
        {
            if (_provider is not null) return _provider;

            var options = secrets.Load()
                ?? throw new OAuthException(
                    "Google のクライアント設定がありません。⚙メニューから読み込んでください。");

            return _provider = new GoogleTokenProvider(
                new LoopbackOAuthFlow(
                    options, _http, openBrowser,
                    log: message => _log.Write("Google 認可", message)),
                tokens);
        }
    }

    /// <summary>同期の本体。初めて呼ばれたときに組み立てる。</summary>
    private GoogleSyncService Service()
    {
        lock (_build)
        {
            return _service ??= new GoogleSyncService(
                workspace,
                new GoogleCalendarApi(_http, Provider(), _retry),
                new GoogleTasksApi(_http, Provider(), _retry),
                from,
                _log);
        }
    }

    /// <summary>失敗を1行で残す。利用者が止めたもの（中止）は、失敗ではなく中止として残す。</summary>
    private void LogFailure(string area, Exception ex, CancellationToken cancellationToken, string? what = null)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            _log.Write(area, $"{(what is null ? string.Empty : what + "を")}中止された");
            return;
        }

        _log.Failure(area, ex, GoogleFailure.Classify(ex), what);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _service?.Dispose();

        if (_ownsHttp)
        {
            _http.Dispose();
            _uploadHttp.Dispose();
        }
    }
}
