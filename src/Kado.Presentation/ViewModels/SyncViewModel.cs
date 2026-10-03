using System.Text.Json;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Net;
using Kado.Presentation.Sync;

namespace Kado.Presentation.ViewModels;

/// <summary>同期の見え方。丸印の色をこれで決める。</summary>
public enum SyncState
{
    /// <summary>繋いでいない。</summary>
    Disconnected,

    /// <summary>繋いであり、落ち着いている。</summary>
    Idle,

    /// <summary>いま走っている。</summary>
    Running,

    /// <summary>直前の同期でうまくいかなかったことがある。</summary>
    Warned,

    /// <summary>直前の同期が失敗した。</summary>
    Failed,
}

/// <summary>
/// 右上の同期表示と、その操作。
/// <para>
/// ここに出すのは<b>同期の状態だけ</b>。操作の結果や断り書きを混ぜると、
/// 同期できているのかどうかが読み取れなくなる。
/// </para>
/// <para>
/// <b>待ち合わせのあとは、呼ばれた場所へ戻る（<c>ConfigureAwait(true)</c>）。</b>
/// このクラスは待ち合わせのあとに状態を書き換え、<see cref="Synced"/> で画面を引き直させる。
/// <c>ConfigureAwait(false)</c> だと別のスレッドのまま画面を触ることになり、WPF が
/// 「このオブジェクトは別のスレッドに所有されているため…」で撥ねる。通信そのものを
/// 行う下の層（<c>GoogleSyncService</c> 以下）は画面を触らないので、あちらは戻らなくてよい。
/// </para>
/// </summary>
public sealed class SyncViewModel : ObservableObject
{
    private readonly IGoogleSync? _google;

    /// <summary>未読の警告を、いくつまで持つか。溜め続けて画面を埋めないための上限。</summary>
    private const int MaxUnreadWarnings = 100;

    private SyncState _state;
    private string? _detail;
    private DateTimeOffset? _lastSyncedAt;

    /// <summary>まだ使う人が見ていない警告。古いものから。同じ文は重ねない。</summary>
    private readonly List<string> _unreadWarnings = [];

    /// <summary>走っている同期を止めるための印。走っていない間は null。</summary>
    private CancellationTokenSource? _syncCts;

    /// <summary>直前の中断が <see cref="CancelSyncCommand"/> によるものか。タイムアウトと区別する。</summary>
    private bool _cancelledByUser;

    /// <summary>
    /// 走っているものの中身。右上の「同期中…」「接続中…」「切断中…」の出し分けに使う。
    /// 中止ボタンはどれにも効く（<see cref="_syncCts"/> を切る）。
    /// </summary>
    private Activity _activity = Activity.Syncing;

    /// <summary>直前の同期が、呼びすぎ（待って出し直してもだめ）で一部を次回に回したか。</summary>
    private bool _lastSyncThrottled;

    private enum Activity
    {
        Syncing,
        Connecting,
        Disconnecting,
    }

    public SyncViewModel(IGoogleSync? google = null)
    {
        _google = google;
        _state = google?.IsConnected == true ? SyncState.Idle : SyncState.Disconnected;

        // 中で必ず状態を更新するので、ここまで例外が来たら想定外。表示に残す
        ConnectCommand = new AsyncRelayCommand(
            () => ConnectAsync(), () => CanConnect && !IsBusy && !IsConnected, ex => Fail(ex.Message));

        DisconnectCommand = new AsyncRelayCommand(
            () => DisconnectAsync(), () => IsConnected && !IsBusy, ex => Fail(ex.Message));

        SyncNowCommand = new AsyncRelayCommand(
            () => SyncAsync(), () => IsConnected && !IsBusy, ex => Fail(ex.Message));

        // 走っている間だけ押せる。ボタン側は同じ場所に「中止」として出す
        CancelSyncCommand = new RelayCommand(RequestCancel, () => IsBusy);

        AcknowledgeWarningsCommand = new RelayCommand(AcknowledgeWarnings, () => HasUnreadWarnings);
    }

    /// <summary>警告を読んだことにして、消す。使う人が見るまで、前回までの警告は残る。</summary>
    public RelayCommand AcknowledgeWarningsCommand { get; }

    /// <summary>繋ぐ。ブラウザが開く。</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>切る。</summary>
    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>いま同期する。</summary>
    public AsyncRelayCommand SyncNowCommand { get; }

    /// <summary>いま走っている同期を中止する。</summary>
    public RelayCommand CancelSyncCommand { get; }

    /// <summary>同期が終わったときに呼ばれる。画面はこれを見て引き直す。</summary>
    public event EventHandler? Synced;

    /// <summary>
    /// 同期が、失敗でも「呼びすぎで一部を次回に回した」でもなく終わったときに呼ばれる。
    /// 手で押した同期でも、裏の同期でも上がる。裏の同期は、これを受けて、失敗で延びた間隔を戻す。
    /// </summary>
    public event EventHandler? Succeeded;

    /// <summary>「Google に接続」の待ち（ブラウザでの認可）を、中止ボタンで止めたときに呼ばれる。</summary>
    public event EventHandler? ConnectCancelled;

    /// <summary>
    /// 中止ボタンで止めたときに呼ばれる。<see cref="Synced"/> は何も変わっていないので流さない
    /// （所属の作り直しなどを走らせる必要が無い）。
    /// </summary>
    public event EventHandler? Cancelled;

    /// <summary>
    /// 中止・失敗で途中から抜けたが、そこまでに手元へ書き込んだかもしれないときに呼ばれる。
    /// <para>
    /// <see cref="Synced"/> は流さない（結果の報告が無く、<see cref="LastReport"/> は前回のまま）。
    /// 画面はこれを受けて、書き込まれたぶんを読み直す。書き込んでいないと分かっているとき
    /// （最初の通信の前に転んだだけ）は呼ばない。
    /// </para>
    /// </summary>
    public event EventHandler? InterruptedAfterWrites;

    public SyncState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(StatusText), nameof(IsConnected), nameof(IsBusy),
                    nameof(ActionLabel), nameof(ActionToolTip), nameof(DetailText));
            }
            RaiseCanExecute();
        }
    }

    /// <summary>繋いであるか。</summary>
    public bool IsConnected => _state is not SyncState.Disconnected;

    /// <summary>いま走っているか。走っている間は操作を止める。</summary>
    public bool IsBusy => _state is SyncState.Running;

    /// <summary>繋げる支度ができているか。</summary>
    public bool CanConnect => _google is { CanConnect: true };

    /// <summary>
    /// 押せるかどうかを見直す。
    /// <para>
    /// 繋げるかどうかは<b>こちらの外側で変わる</b>。クライアント設定を読み込んだ時点で
    /// 繋げるようになるが、これらのコマンドは WPF の <c>CommandManager</c> に乗って
    /// いないので、黙っていると無効のままになる。設定を入れたのに「Google に接続…」が
    /// 押せない、という状態を防ぐために呼ぶ。
    /// </para>
    /// </summary>
    public void RefreshAvailability()
    {
        Raise(nameof(CanConnect));
        RaiseCanExecute();
    }

    /// <summary>最後に同期できた時刻。一度も成功していなければ null。</summary>
    public DateTimeOffset? LastSyncedAt
    {
        get => _lastSyncedAt;
        private set
        {
            if (Set(ref _lastSyncedAt, value)) Raise(nameof(StatusText));
        }
    }

    /// <summary>
    /// 右上に出す文。
    /// <para>失敗したときだけ理由を添える。うまくいっているときは時刻だけでよい。</para>
    /// </summary>
    public string StatusText => _state switch
    {
        SyncState.Disconnected => "Google 未接続",
        SyncState.Running => _activity switch
        {
            Activity.Connecting => "接続中…",
            Activity.Disconnecting => "切断中…",
            _ => "同期中…",
        },
        SyncState.Failed => $"同期できません（{_detail}）",
        SyncState.Warned => $"一部を伝えられません（{WarningSummary}）",
        _ => _lastSyncedAt is { } at ? $"同期済み {at.ToLocalTime():HH:mm}" : "同期済み",
    };

    // ------------------------------------------------------------------
    // 警告。最初の1件だけでなく、件数と全部を読めるようにする
    // ------------------------------------------------------------------

    /// <summary>
    /// まだ見ていない警告。
    /// <para>
    /// 次の同期で警告が無くても、<see cref="AcknowledgeWarningsCommand"/> で読んだことにするまで
    /// 消さない。裏で15分ごとに回るので、気づく前に次の同期で消えてしまうと、何が伝わらなかったのか
    /// 知るすべが無い。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Warnings => _unreadWarnings.ToArray();

    /// <summary>未読の警告の件数。</summary>
    public int WarningCount => _unreadWarnings.Count;

    /// <summary>未読の警告があるか。</summary>
    public bool HasUnreadWarnings => _unreadWarnings.Count > 0;

    /// <summary>警告の見出し。「警告 3件」。</summary>
    public string WarningHeader => $"警告 {_unreadWarnings.Count}件";

    /// <summary>警告を1行ずつ並べた全文。ツールチップや一覧に出す。無ければ空。</summary>
    public string WarningsText => string.Join(
        Environment.NewLine, _unreadWarnings.Select((warning, index) => $"{index + 1}. {warning}"));

    /// <summary>状態の文に、警告の全文を添えたもの。ツールチップに出す。</summary>
    public string DetailText => HasUnreadWarnings
        ? $"{StatusText}{Environment.NewLine}{Environment.NewLine}{WarningHeader}{Environment.NewLine}{WarningsText}"
        : StatusText;

    /// <summary>状態の文に添える、警告の要約。最初の1件と、残りの件数。</summary>
    private string WarningSummary => _unreadWarnings.Count switch
    {
        0 => string.Empty,
        1 => _unreadWarnings[0],
        var count => $"{_unreadWarnings[0]} ほか{count - 1}件",
    };

    /// <summary>今回の警告を未読に足す。同じ文は重ねない。</summary>
    private void AddWarnings(IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning) || _unreadWarnings.Contains(warning)) continue;

            _unreadWarnings.Add(warning);
        }

        // 上限を超えたら、古いほうから落とす
        if (_unreadWarnings.Count > MaxUnreadWarnings)
        {
            _unreadWarnings.RemoveRange(0, _unreadWarnings.Count - MaxUnreadWarnings);
        }

        RaiseWarnings();
    }

    /// <summary>警告を読んだことにする。警告の表示だった状態は、落ち着いた状態に戻す。</summary>
    private void AcknowledgeWarnings()
    {
        _unreadWarnings.Clear();
        RaiseWarnings();

        if (_state == SyncState.Warned) State = SyncState.Idle;
    }

    private void RaiseWarnings()
    {
        Raise(nameof(Warnings), nameof(WarningCount), nameof(HasUnreadWarnings), nameof(WarningHeader),
            nameof(WarningsText), nameof(DetailText), nameof(StatusText), nameof(ActionLabel));

        AcknowledgeWarningsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// ボタンに出す文言。走っている間は「中止」にする。
    /// <para>押すと止められることをその場で示す。右上の状態表示（<see cref="StatusText"/>）は
    /// 「同期中…」のまま変えない。ボタンでの操作案内と状態表示は役目が違う。</para>
    /// </summary>
    public string ActionLabel => IsBusy ? "中止" : StatusText;

    /// <summary>ボタンのツールチップ。走っている間だけ、押すと止まることを伝える。</summary>
    public string ActionToolTip => IsBusy ? "押すと同期を中止します" : "押すと今すぐ同期します";

    /// <summary>直前の同期の結果。詳しく見せるとき用。</summary>
    public SyncReport? LastReport { get; private set; }

    /// <summary>
    /// 裏で静かに同期する。
    /// <para>
    /// 手で押したときと違い、利用者が頼んでいない。<b>失敗しても画面を割り込ませない。</b>
    /// 状態は右上の表示に出るので、気づける人は気づく。
    /// </para>
    /// </summary>
    /// <returns>うまくいったら true。次の間隔を決めるのに使われる。</returns>
    public async Task<bool> SyncQuietlyAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null || !_google.IsConnected || IsBusy) return true;

        await SyncAsync(cancellationToken).ConfigureAwait(true);

        // 警告どまりなら、繋がってはいる。間隔を伸ばす理由にはしない。
        // ただし、呼びすぎ（429）で一部を次回に回したときは、すぐ次を叩いても同じ結果になりやすい。
        // 失敗と同じに数えて、間隔を延ばす（警告どまりなので State は Warned のまま）
        return State is not SyncState.Failed && !_lastSyncThrottled;
    }

    /// <summary>
    /// 繋ぐ。
    /// <para>
    /// 認可を待つあいだ（最長5分）も、中止ボタンで止められる（<see cref="CancelSyncCommand"/>）。
    /// 止めたときは失敗にせず、未接続のまま戻る（<see cref="ConnectCancelled"/>）。
    /// </para>
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        _activity = Activity.Connecting;
        State = SyncState.Running;

        // 中止ボタンはこの印を切る（同期のときと同じ仕掛け）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _syncCts = cts;
        _cancelledByUser = false;

        try
        {
            await _google.ConnectAsync(cts.Token).ConfigureAwait(true);

            State = SyncState.Idle;

            // 繋いだらすぐ取り込む。繋いだのに何も出ないと、繋がったのか分からない
            _syncCts = null;
            await SyncAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cancelledByUser)
        {
            // 認可の待ちを中止ボタンで止めた。失敗ではない。繋がっていなければ、未接続に戻る
            State = _google.IsConnected ? SyncState.Idle : SyncState.Disconnected;
            ConnectCancelled?.Invoke(this, EventArgs.Empty);
        }
        catch (OAuthException ex)
        {
            Fail(ex.WasDeclined ? "許可されませんでした" : ex.Message);
        }
        catch (Exception ex)
        {
            // 通信の失敗は原因ごとに言い分ける（プロキシの認証・証明書・接続・時間切れ・応答が読めない）。
            // 想定していない転び方をしても、繋がっていない扱いのまま止める。型名は出さない
            Fail(GoogleFailure.Describe(ex, ex is HttpRequestException ? "ネットワークに繋がりません" : "接続できませんでした"));
        }
        finally
        {
            // catch で拾い切れない抜け方をしても、Running のまま残さない
            if (State == SyncState.Running) State = SyncState.Idle;

            if (ReferenceEquals(_syncCts, cts)) _syncCts = null;
            _cancelledByUser = false;
            _activity = Activity.Syncing;
        }
    }

    /// <summary>
    /// Google 側の許可の取り消しが届かなかったときの案内。
    /// <para>こちらの接続は切れている。向こうに許可が残っているので、手で外してもらう。</para>
    /// </summary>
    public const string RevocationNotDeliveredMessage =
        "接続は切りました。ただし、Google 側の許可の取り消しは届きませんでした。"
        + "Google アカウントの設定（セキュリティ → サードパーティのアプリとサービス）から Kado を外してください。";

    /// <summary>切ったが、Google 側の取り消しが届かなかったときに呼ばれる。案内を出す先。</summary>
    public event EventHandler? RevocationNotDelivered;

    /// <summary>
    /// 切る。
    /// <para>
    /// 取り消しの通信（最大30秒）のあいだは「切断中…」で、中止ボタンで通信だけを諦められる。
    /// 諦めても、こちらの接続は切れる（取り消しが届かなかった案内を出す）。
    /// </para>
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        _activity = Activity.Disconnecting;
        State = SyncState.Running;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _syncCts = cts;
        _cancelledByUser = false;

        var revoked = true;
        try
        {
            revoked = await _google.DisconnectAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 取り消しの通信の失敗は、実物の側が受けて戻り値で返す。ここへ来るのは、
            // 控えを消す段で転んだ場合や、取り消しの通信を止めた場合など。
            // 控えが消えていれば切れている（取り消しは届いていない扱い）
            revoked = false;

            // 控えが残っているのに「切った」と見せると、裏の同期が勝手に再開する。正直に失敗にする
            if (_google.IsConnected)
            {
                Fail("切断できませんでした");
                return;
            }
        }
        finally
        {
            if (ReferenceEquals(_syncCts, cts)) _syncCts = null;
            _cancelledByUser = false;
            _activity = Activity.Syncing;
        }

        LastReport = null;
        LastSyncedAt = null;

        // 切った相手の警告は、もう読む意味が無い
        _unreadWarnings.Clear();
        RaiseWarnings();

        State = SyncState.Disconnected;

        Synced?.Invoke(this, EventArgs.Empty);

        if (!revoked) RevocationNotDelivered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一度だけ同期する。</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null || !_google.IsConnected) return;

        _activity = Activity.Syncing;
        State = SyncState.Running;
        _lastSyncThrottled = false;

        // 中止ボタンはこの印を切る。外から渡された cancellationToken（裏の定期同期が
        // 持つ既定のもの）とは別物なので、繋いだものを作って両方を見張る
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _syncCts = cts;
        _cancelledByUser = false;

        try
        {
            var report = await _google.SyncAsync(cts.Token).ConfigureAwait(true);

            // すでに走っていた。状態は走っているほうが持っている
            if (report is null)
            {
                State = SyncState.Idle;
                return;
            }

            LastReport = report;
            LastSyncedAt = DateTimeOffset.Now;
            _lastSyncThrottled = report.Throttled;

            // 警告は溜める。今回が無事でも、前回までの警告を使う人が見るまで消さない
            AddWarnings(report.Warnings);

            State = HasUnreadWarnings ? SyncState.Warned : SyncState.Idle;

            Synced?.Invoke(this, EventArgs.Empty);

            // 呼びすぎで一部を次回に回したときは、うまくいったことにしない（間隔を戻さない）
            if (!report.Throttled) Succeeded?.Invoke(this, EventArgs.Empty);
        }
        // 中止ボタンで止めた。失敗ではないので赤くしない。タイムアウト（下の catch）とは
        // 区別するので、こちらを先に置く（型が同じなので条件の無いほうが後だと拾えない）
        catch (OperationCanceledException) when (_cancelledByUser)
        {
            State = SyncState.Idle;
            Cancelled?.Invoke(this, EventArgs.Empty);
            RaiseInterruptedIfWritten();
        }
        catch (OAuthException ex)
        {
            // 更新トークンが死んでいたら繋ぎ直しが要る
            Fail(ex.IsRefreshTokenDead ? "繋ぎ直してください" : ex.Message);
            RaiseInterruptedIfWritten();
        }
        catch (Exception ex)
        {
            // 失敗は原因ごとに言い分ける（プロキシの認証・証明書・接続できない・時間切れ・Google が拒否・
            // 呼びすぎ・応答が読めない）。「ネットワークに繋がりません」でひとまとめにしない。
            // 想定していない転び方をしても、「同期中…」のまま固まらせない。型名は画面に出さない
            // （詳しい連鎖は GoogleConnection が shell.log に残している）
            Fail(GoogleFailure.Describe(ex, ex is HttpRequestException ? "ネットワークに繋がりません" : "同期できませんでした"));
            RaiseInterruptedIfWritten();
        }
        finally
        {
            // catch で拾い切れない抜け方をしても、Running のまま残さない。
            // ここが無いと、以後の同期も IsBusy に阻まれて二度と走らなくなる
            if (State == SyncState.Running) State = SyncState.Idle;

            // この同期の印を片付ける。中止ボタンは次の同期が始まるまで押せない
            _syncCts = null;
            _cancelledByUser = false;
        }
    }

    /// <summary>
    /// 途中で抜けた同期が、手元へ書き込んでいたかもしれなければ知らせる。
    /// <para>
    /// 件数の報告（<see cref="SyncReport"/>）は例外で失われるので、書いたかどうかは繋ぎの側
    /// （<see cref="IGoogleSync.MayHaveWrittenBeforeInterruption"/>）に聞く。聞けないときは読み直す側に倒す。
    /// </para>
    /// </summary>
    private void RaiseInterruptedIfWritten()
    {
        if (_google is { MayHaveWrittenBeforeInterruption: false }) return;

        InterruptedAfterWrites?.Invoke(this, EventArgs.Empty);
    }

    private void RequestCancel()
    {
        if (_syncCts is not { IsCancellationRequested: false } cts) return;

        _cancelledByUser = true;
        cts.Cancel();
    }

    private void Fail(string detail)
    {
        _detail = detail;
        State = SyncState.Failed;
    }

    private void RaiseCanExecute()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SyncNowCommand.RaiseCanExecuteChanged();
        CancelSyncCommand.RaiseCanExecuteChanged();
    }
}
