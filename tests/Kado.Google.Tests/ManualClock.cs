namespace Kado.Google.Tests;

/// <summary>
/// 手で進める時計。
/// <para>
/// タスクの差分は <c>updatedMin</c> で取るので、同期の側と相手の側で時計が
/// 食い違うと、変わったものが降ってこない。試験では同じ時計を共有する。
/// </para>
/// </summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>時計を進める。</summary>
    public DateTimeOffset Advance(TimeSpan amount) => _now = _now.Add(amount);
}
