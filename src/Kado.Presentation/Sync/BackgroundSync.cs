namespace Kado.Presentation.Sync;

/// <summary>
/// 決まった間隔で静かに同期する。
/// <para>
/// <b>静かに、というのが肝心。</b>手で押した同期と違い、こちらは利用者が頼んでいない。
/// 失敗しても画面を割り込ませない。状態は右上の表示に出るので、気づける人は気づく。
/// </para>
/// <para>
/// 時計を注いで動かすので、試験では実時間を待たずに進められる。
/// </para>
/// </summary>
public sealed class BackgroundSync : IDisposable
{
    /// <summary>既定の間隔。短すぎると呼び出し回数の上限に当たる。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 失敗が続いたときに、どこまで間隔を伸ばすか。
    /// <para>繋がらない状態で15分おきに叩き続けても、電池と回線を使うだけ。</para>
    /// </summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);

    /// <summary>
    /// スリープから戻ってから、同期を起こすまでの待ち。
    /// <para>
    /// 戻った直後は、Wi-Fi・VPN・プロキシがまだ繋がっていない。すぐ叩くと、接続できずに失敗として数えられ、
    /// 間隔が延びてしまう。ネットワークが戻るのに足りる、数十秒を待つ。
    /// </para>
    /// </summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(45);

    private readonly Func<CancellationToken, Task<bool>> _sync;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;

    /// <summary>待ちを中断する印・次の待ち・失敗の数えは、画面のスレッドと裏のループの両方から触る。</summary>
    private readonly object _lock = new();

    private CancellationTokenSource? _running;
    private CancellationTokenSource? _nudge;
    private TimeSpan? _nextWait;
    private int _failures;

    /// <param name="sync">同期の本体。うまくいったら true を返す。</param>
    /// <param name="interval">間隔。省略すると15分。</param>
    /// <param name="clock">時計。試験では進められるものを渡す。</param>
    public BackgroundSync(
        Func<CancellationToken, Task<bool>> sync,
        TimeSpan? interval = null,
        TimeProvider? clock = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>動いているか。</summary>
    public bool IsRunning => _running is { IsCancellationRequested: false };

    /// <summary>これまでに走った回数。</summary>
    public int Runs { get; private set; }

    /// <summary>いま待っている長さ。失敗が続くと伸びる。</summary>
    public TimeSpan CurrentDelay
    {
        get
        {
            lock (_lock) return Delay(_failures);
        }
    }

    /// <summary>
    /// 少し待ってから、1回だけ同期を起こす。スリープから戻ったときに呼ぶ。
    /// <para>
    /// 眠っているあいだタイマーは止まっているので、戻っても、次の間隔（最長2時間まで延びていることがある）が
    /// 来るまで追いつかない。待っている長さを <paramref name="delay"/> に差し替えて起こし、走ったあとは
    /// 通常の間隔に戻る。待ち直すだけなので、同期が走っている最中に呼んでも二重には走らない。
    /// </para>
    /// </summary>
    /// <param name="delay">起こすまでの待ち。省略すると <see cref="ResumeDelay"/>。</param>
    public void SyncSoon(TimeSpan? delay = null)
    {
        lock (_lock)
        {
            if (!IsRunning) return;

            _nextWait = delay ?? ResumeDelay;
            _nudge?.Cancel();
        }
    }

    /// <summary>
    /// 手で押した同期がうまくいったことを知らせる。失敗で延びた間隔を、通常に戻す。
    /// <para>
    /// 失敗の数えは、裏の同期の結果でしか戻らなかった。繋がらない間に延びた間隔が、手で同期して
    /// 繋がっているのを確かめたあとも、最長2時間のまま残る。
    /// </para>
    /// </summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            if (_failures == 0) return;

            _failures = 0;

            // いま待っている長い間隔を捨てて、通常の間隔で待ち直す
            if (IsRunning)
            {
                _nextWait = _interval;
                _nudge?.Cancel();
            }
        }
    }

    /// <summary>始める。すでに動いていれば何もしない。</summary>
    public void Start()
    {
        if (IsRunning) return;

        _running = new CancellationTokenSource();
        _ = LoopAsync(_running.Token);
    }

    /// <summary>止める。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;

            _nudge?.Dispose();
            _nudge = null;
            _nextWait = null;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            CancellationTokenSource nudge;

            lock (_lock)
            {
                // 呼び起こされていれば、その長さ。無ければ、失敗の数に応じた通常の間隔
                wait = _nextWait ?? Delay(_failures);
                _nextWait = null;

                _nudge?.Dispose();
                nudge = _nudge = new CancellationTokenSource();
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, nudge.Token);
                await Task.Delay(wait, _clock, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return;

                // 呼び起こされた（スリープからの復帰、手で同期が通った）。待ち直す
                continue;
            }
            catch (ObjectDisposedException)
            {
                // 止められた
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                Runs++;
                var ok = await _sync(cancellationToken).ConfigureAwait(false);

                lock (_lock) _failures = ok ? 0 : _failures + 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 自前の停止トークンが立っている。Stop() が呼ばれたということなので終える
                return;
            }
            catch (Exception)
            {
                // 頼まれていない同期で落ちるのが、いちばん困る。次の回に賭ける。
                // HttpClient のタイムアウトなど、こちらの停止指示ではない
                // OperationCanceledException もここに落ちる。ループそのものは終わらせない
                lock (_lock) _failures++;
            }
        }
    }

    /// <summary>
    /// 次まで待つ長さ。
    /// <para>失敗するたびに倍にして、上限で止める。繋がらないのに叩き続けない。</para>
    /// </summary>
    private TimeSpan Delay(int failures)
    {
        if (failures <= 0) return _interval;

        // 倍にしていくが、桁が溢れないよう先に上限で切る
        var multiplier = Math.Min(1L << Math.Min(failures, 10), MaxBackoff.Ticks / Math.Max(_interval.Ticks, 1));
        var delay = TimeSpan.FromTicks(_interval.Ticks * Math.Max(multiplier, 1));

        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    public void Dispose() => Stop();
}
