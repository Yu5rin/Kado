using System.Net;

namespace Kado.Google.Sync;

/// <summary>
/// Google の API が 429（呼びすぎ）や 5xx（向こうの不調）を返したときに、待って出し直す方針。
/// <para>
/// <b>待つ長さは <c>Retry-After</c>。</b>無ければ 2 秒・4 秒・8 秒と倍にする（1回ぶんの上限
/// <see cref="MaxDelay"/>）。出し直すのは数回だけ（<see cref="MaxRetries"/>）で、1回の同期で
/// 待つ合計にも上限（<see cref="TotalBudget"/>）を置く。待ちきれないほど長い <c>Retry-After</c> を
/// 言われたときは、待たずに諦める（画面を何分も固めない。次回の同期に回す）。
/// </para>
/// <para>
/// <b>出し直してはいけない場面がある。</b>作成（POST）は、向こうが受け付けたのに応答だけが
/// 失われたとき、出し直すと二重に作る。そのため POST は、確実に「受け付けていない」と言える
/// 429 と 503 と、呼びすぎの 403 だけを出し直す。500・502・504 は、読み取り・書き換え（PATCH）・
/// 削除など、もう一度送っても結果が変わらないものだけ。
/// </para>
/// <para>
/// 待ち方は差し替えられる。試験では実時間を待たずに、待つはずだった長さだけを受け取る。
/// </para>
/// </summary>
public sealed class GoogleRetryPolicy(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>出し直しの回数の上限（最初の1回は数えない）。</summary>
    public const int MaxRetries = 3;

    /// <summary>1回ぶんの待ちの上限。これより長い <c>Retry-After</c> は、待たずに諦める。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>最初の待ち。以後は倍にする。</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>1回の同期で待つ合計の上限。</summary>
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(60);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? DefaultDelay;

    private long _waitedTicks;

    /// <summary>本物の待ち方で動く既定の方針。</summary>
    public static GoogleRetryPolicy Default => new();

    /// <summary>出し直さない方針。待たずにすぐ結果を返したい試験や、1回で十分な呼び出しに使う。</summary>
    public static GoogleRetryPolicy None { get; } = new(maxRetries: 0);

    private readonly int _maxRetries = MaxRetries;

    private GoogleRetryPolicy(int maxRetries) : this(null) => _maxRetries = maxRetries;

    private static Task DefaultDelay(TimeSpan span, CancellationToken cancellationToken) =>
        Task.Delay(span, cancellationToken);

    /// <summary>これまでに待った合計。</summary>
    public TimeSpan Waited => TimeSpan.FromTicks(Interlocked.Read(ref _waitedTicks));

    /// <summary>待った合計を数え直す。同期の最初に呼ぶ。</summary>
    public void ResetBudget() => Interlocked.Exchange(ref _waitedTicks, 0);

    /// <summary>
    /// 出し直すなら、待つ長さ。出し直さないなら null。
    /// </summary>
    /// <param name="method">要求の種類。POST は出し直す場面を絞る。</param>
    /// <param name="status">応答の状態。</param>
    /// <param name="isRateLimited403">403 のうち、呼びすぎ・容量の上限だったもの。</param>
    /// <param name="retryAfter">応答の <c>Retry-After</c>。</param>
    /// <param name="retriesSoFar">これまでに出し直した回数。</param>
    public TimeSpan? NextDelay(
        HttpMethod method, HttpStatusCode status, bool isRateLimited403, TimeSpan? retryAfter, int retriesSoFar)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (retriesSoFar >= _maxRetries) return null;
        if (!ShouldRetry(method, status, isRateLimited403)) return null;

        var wait = retryAfter ?? TimeSpan.FromTicks(BaseDelay.Ticks << Math.Min(retriesSoFar, 10));

        // 向こうが「長く待て」と言っているのに短く出し直しても、また断られるだけ。次回の同期に回す
        if (wait > MaxDelay) return null;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

        if (Waited + wait > TotalBudget) return null;

        return wait;
    }

    /// <summary>この種類の要求を、この結果のときに出し直してよいか。</summary>
    public static bool ShouldRetry(HttpMethod method, HttpStatusCode status, bool isRateLimited403)
    {
        if (status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.ServiceUnavailable) return true;
        if (status == HttpStatusCode.Forbidden) return isRateLimited403;

        if (status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            // 作成は、向こうが受け付けたのに応答だけ失われた場合がある。出し直すと二重に作る
            return method != HttpMethod.Post;
        }

        return false;
    }

    /// <summary>待つ。待った長さは合計に数える。</summary>
    public async Task WaitAsync(TimeSpan span, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _waitedTicks, span.Ticks);

        if (span > TimeSpan.Zero) await _delay(span, cancellationToken).ConfigureAwait(false);
    }
}
