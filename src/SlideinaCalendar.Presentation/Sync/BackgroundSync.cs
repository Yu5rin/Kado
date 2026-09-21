namespace SlideinaCalendar.Presentation.Sync;

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

    private readonly Func<CancellationToken, Task<bool>> _sync;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;

    private CancellationTokenSource? _running;
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
    public TimeSpan CurrentDelay => Delay(_failures);

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
        _running?.Cancel();
        _running?.Dispose();
        _running = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Delay(_failures), _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                Runs++;
                _failures = await _sync(cancellationToken).ConfigureAwait(false) ? 0 : _failures + 1;
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
                _failures++;
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
