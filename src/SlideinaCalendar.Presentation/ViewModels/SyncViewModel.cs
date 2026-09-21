using System.Text.Json;
using SlideinaCalendar.Google.OAuth;
using SlideinaCalendar.Google.Sync;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.Sync;

namespace SlideinaCalendar.Presentation.ViewModels;

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

    private SyncState _state;
    private string? _detail;
    private DateTimeOffset? _lastSyncedAt;

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
    }

    /// <summary>繋ぐ。ブラウザが開く。</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>切る。</summary>
    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>いま同期する。</summary>
    public AsyncRelayCommand SyncNowCommand { get; }

    /// <summary>同期が終わったときに呼ばれる。画面はこれを見て引き直す。</summary>
    public event EventHandler? Synced;

    public SyncState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value)) Raise(nameof(StatusText), nameof(IsConnected), nameof(IsBusy));
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
        SyncState.Running => "同期中…",
        SyncState.Failed => $"同期できません（{_detail}）",
        SyncState.Warned => $"一部を伝えられません（{_detail}）",
        _ => _lastSyncedAt is { } at ? $"同期済み {at.ToLocalTime():HH:mm}" : "同期済み",
    };

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

        // 警告どまりなら、繋がってはいる。間隔を伸ばす理由にはしない
        return State is not SyncState.Failed;
    }

    /// <summary>繋ぐ。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        State = SyncState.Running;
        try
        {
            await _google.ConnectAsync(cancellationToken).ConfigureAwait(true);

            State = SyncState.Idle;

            // 繋いだらすぐ取り込む。繋いだのに何も出ないと、繋がったのか分からない
            await SyncAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OAuthException ex)
        {
            Fail(ex.WasDeclined ? "許可されませんでした" : ex.Message);
        }
        catch (HttpRequestException)
        {
            Fail("ネットワークに繋がりません");
        }
        catch (OperationCanceledException)
        {
            // ブラウザでの認可待ちがタイムアウトした、あるいは打ち切られた
            Fail("時間内に応答がありませんでした");
        }
        catch (JsonException)
        {
            Fail("応答を読み取れませんでした（ネットワークの接続先を確認してください）");
        }
        catch (Exception)
        {
            // 想定していない転び方をしても、繋がっていない扱いのまま止める
            Fail("接続できませんでした");
        }
        finally
        {
            // catch で拾い切れない抜け方をしても、Running のまま残さない
            if (State == SyncState.Running) State = SyncState.Idle;
        }
    }

    /// <summary>切る。</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        try
        {
            await _google.DisconnectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OAuthException or HttpRequestException)
        {
            // 取り消しに失敗しても、こちらの控えは消えている。繋いでいない扱いでよい
        }

        LastReport = null;
        LastSyncedAt = null;
        State = SyncState.Disconnected;

        Synced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一度だけ同期する。</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null || !_google.IsConnected) return;

        State = SyncState.Running;
        try
        {
            var report = await _google.SyncAsync(cancellationToken).ConfigureAwait(true);

            // すでに走っていた。状態は走っているほうが持っている
            if (report is null)
            {
                State = SyncState.Idle;
                return;
            }

            LastReport = report;
            LastSyncedAt = DateTimeOffset.Now;

            if (report.Warnings.Count > 0)
            {
                _detail = report.Warnings[0];
                State = SyncState.Warned;
            }
            else
            {
                State = SyncState.Idle;
            }

            Synced?.Invoke(this, EventArgs.Empty);
        }
        catch (OAuthException ex)
        {
            // 更新トークンが死んでいたら繋ぎ直しが要る
            Fail(ex.IsRefreshTokenDead ? "繋ぎ直してください" : ex.Message);
        }
        catch (GoogleApiException ex)
        {
            Fail(ex.Reason);
        }
        catch (HttpRequestException)
        {
            Fail("ネットワークに繋がりません");
        }
        catch (OperationCanceledException)
        {
            // HttpClient の既定タイムアウト超過などで TaskCanceledException が来る場面を含む
            Fail("時間内に応答がありませんでした");
        }
        catch (JsonException)
        {
            // キャプティブポータルなどが HTML を 200 で返し、応答を JSON として読めない場面
            Fail("応答を読み取れませんでした（ネットワークの接続先を確認してください）");
        }
        catch (Exception)
        {
            // 想定していない転び方をしても、「同期中…」のまま固まらせない
            Fail("同期できませんでした");
        }
        finally
        {
            // catch で拾い切れない抜け方をしても、Running のまま残さない。
            // ここが無いと、以後の同期も IsBusy に阻まれて二度と走らなくなる
            if (State == SyncState.Running) State = SyncState.Idle;
        }
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
    }
}
