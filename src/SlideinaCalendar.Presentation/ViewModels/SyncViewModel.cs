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

    /// <summary>繋ぐ。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        State = SyncState.Running;
        try
        {
            await _google.ConnectAsync(cancellationToken).ConfigureAwait(false);

            State = SyncState.Idle;

            // 繋いだらすぐ取り込む。繋いだのに何も出ないと、繋がったのか分からない
            await SyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthException ex)
        {
            Fail(ex.WasDeclined ? "許可されませんでした" : ex.Message);
        }
        catch (HttpRequestException)
        {
            Fail("ネットワークに繋がりません");
        }
    }

    /// <summary>切る。</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_google is null) return;

        try
        {
            await _google.DisconnectAsync(cancellationToken).ConfigureAwait(false);
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
            var report = await _google.SyncAsync(cancellationToken).ConfigureAwait(false);

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
