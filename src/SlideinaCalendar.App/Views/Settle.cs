using System.Windows.Threading;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 手が止まってから1回だけ走らせる。
/// <para>
/// ウィンドウや仕切りを動かしているあいだ、<c>SizeChanged</c> は1ドラッグで何十回も
/// 来る。そのたびに月のマスを組み直したり、年の12か月を並べ替えたりしていたので、
/// 掴んで動かすと画面が固まっていた。<b>動かしている最中は数えるだけにして、
/// 止まってから組み直す。</b>
/// </para>
/// </summary>
internal sealed class Settle : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _work;

    private bool _disposed;

    /// <param name="work">落ち着いたときに走らせるもの。</param>
    /// <param name="delay">これだけ手が止まったら走らせる。</param>
    internal Settle(Action work, TimeSpan? delay = null)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = delay ?? TimeSpan.FromMilliseconds(120),
        };

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _work();
        };
    }

    /// <summary>また動いた。数え直す。</summary>
    internal void Poke()
    {
        if (_disposed) return;

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>待たずに今すぐ走らせる。</summary>
    internal void Now()
    {
        if (_disposed) return;

        _timer.Stop();
        _work();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer.Stop();
    }
}
