using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Kado.App.Shell;

/// <summary>
/// ログの行を、呼んだスレッドでは行列に積むだけにして、別のスレッドがまとめて書く。
/// <para>
/// 以前は1行ごとに、呼んだスレッド（多くは画面のスレッド）で、フォルダの作成・ファイルの
/// 大きさの確認・追記をしていた。ピン留め中の窓の移動通知や、開く演出のコマごとに来るので、
/// ウイルス対策ソフトのファイル検査がある会社の PC では、そのたびに画面が引っかかる。
/// </para>
/// <para>
/// <b>書き込みは遅れる。</b>落ちる直前の行を失わないよう、終わるとき・異常終了のときは
/// <see cref="Flush"/> で吐き出す（<c>App.OnExit</c>・<c>ReportFatal</c>・プロセス終了）。
/// </para>
/// <para>
/// <b>高頻度の行は間引く。</b>呼び出しが<c>key</c>を付けた行は、同じ鍵のものを
/// <c>interval</c> あたり1行だけ書き、残りは「n 回省略、これが最後の値」の1行にまとめる。
/// 鍵を付けない行（起動の計時・更新・Google・配信など、切り分けに要る行）は、
/// 1行も落とさない。
/// </para>
/// <para>
/// <b>記録に失敗してもアプリの動作に影響させない。</b>例外はすべて握りつぶす。
/// WPF に依存しない（Linux のテストにそのまま取り込める）。
/// </para>
/// </summary>
internal sealed class BufferedLogWriter : IDisposable
{
    /// <summary>1回の書き込みにまとめる文字数の目安。これを超えたら、いったん書き出す。</summary>
    private const int ChunkChars = 32 * 1024;

    /// <summary>
    /// 書けないまま溜まる行の上限。書き手が止まったときに、メモリを食い尽くさないための歯止め。
    /// </summary>
    private const int MaxQueued = 50_000;

    /// <summary>書き手が起きてから、行がたまるのを待つ時間。1回の書き込みにまとめて検査を減らす。</summary>
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<string> _pathProvider;
    private readonly long _maxBytes;
    private readonly int _generations;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _useThread;

    private readonly ConcurrentQueue<Entry> _queue = new();
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>書き出しと、間引きの状態を守る。書き手のスレッドと <see cref="Flush"/> が取り合う。</summary>
    private readonly object _ioGate = new();

    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    private int _queued;
    private int _dropped;
    private int _threadStarted;
    private volatile bool _stopped;
    private volatile bool _hasPendingRuns;

    // 以下は _ioGate の中だけで触る
    private string? _path;
    private bool _directoryReady;
    private long _size = -1;

    /// <param name="pathProvider">
    /// 現行ファイルの場所。<b>書き手のスレッドで最初に書くときに</b>呼ぶ
    /// （保存先の決定にはフォルダの確認が伴うので、呼んだスレッドでは行わない）。
    /// </param>
    /// <param name="maxBytes">1つのファイルの大きさの上限。超えたら世代を送る。</param>
    /// <param name="generations">残す世代の数（現行を含む）。</param>
    /// <param name="interval">間引きの間隔。同じ鍵の行は、この間に1行だけ書く。</param>
    /// <param name="clock">時刻の取り方（テスト用）。</param>
    /// <param name="useThread">false なら書き手のスレッドを起こさず、<see cref="Flush"/> でだけ書く（テスト用）。</param>
    public BufferedLogWriter(
        Func<string> pathProvider,
        long maxBytes = 1024 * 1024,
        int generations = 3,
        TimeSpan? interval = null,
        Func<DateTimeOffset>? clock = null,
        bool useThread = true)
    {
        _pathProvider = pathProvider;
        _maxBytes = Math.Max(1, maxBytes);
        _generations = Math.Max(1, generations);
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _clock = clock ?? (() => DateTimeOffset.Now);
        _useThread = useThread;
    }

    /// <summary>
    /// 1行、時刻付きで行列に積む。<b>ファイルには触れない。</b>失敗しても投げない。
    /// </summary>
    /// <param name="message">1行の中身。</param>
    /// <param name="key">
    /// 付ければ、同じ鍵の行を間引く対象にする。付けない行は必ず書く。
    /// </param>
    public void Enqueue(string message, string? key = null)
    {
        if (_stopped) return;

        try
        {
            // 行列が詰まったら、書き手が追い付くまで捨てる（取りこぼした数は、次に書くとき1行にする）
            if (Volatile.Read(ref _queued) >= MaxQueued)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            _queue.Enqueue(new Entry(_clock(), message, key));
            Interlocked.Increment(ref _queued);

            if (_useThread)
            {
                EnsureThread();
                _wake.Set();
            }
        }
        catch
        {
            // 記録できなくても、呼んだ側の動作は止めない
        }
    }

    /// <summary>
    /// 行列に残っているものを、いまのスレッドで書き切る。間引き中の行の要約も書く。
    /// 終わるとき・異常終了のときに呼ぶ。何度呼んでもよい。
    /// </summary>
    public void Flush()
    {
        try
        {
            lock (_ioGate)
            {
                Drain(emitPending: true);
            }
        }
        catch
        {
            // 記録できなくても、終了の邪魔はしない
        }
    }

    public void Dispose()
    {
        if (_stopped) return;

        Flush();
        _stopped = true;
        _wake.Set();
    }

    private void EnsureThread()
    {
        if (Interlocked.CompareExchange(ref _threadStarted, 1, 0) != 0) return;

        var thread = new Thread(Loop)
        {
            // 画面が終われば、これも終わる（終わるときの吐き出しは Flush が受け持つ）
            IsBackground = true,
            Name = "Kado.ShellLog",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private void Loop()
    {
        while (!_stopped)
        {
            try
            {
                // 間引き中の行があれば、一定時間なにも来ないことで「流れが止まった」と見て要約を書く
                var timeout = _hasPendingRuns ? _interval : Timeout.InfiniteTimeSpan;
                var signalled = _wake.Wait(timeout);

                if (_stopped) break;

                // 続けて来る行を1回の書き込みにまとめる
                if (signalled) Thread.Sleep(BatchDelay);

                _wake.Reset();

                lock (_ioGate)
                {
                    Drain(emitPending: !signalled);
                }
            }
            catch
            {
                // 書き手が死ぬと、以後の行がすべて失われる。何があっても続ける
            }
        }
    }

    /// <summary>行列を空にして書く。<c>_ioGate</c> を持って呼ぶ。</summary>
    private void Drain(bool emitPending)
    {
        var text = new StringBuilder();

        while (_queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _queued);
            Process(entry, text);

            if (text.Length < ChunkChars) continue;

            WriteOut(text.ToString());
            text.Clear();
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            AppendLine(text, _clock(), $"（書き手が追い付かず、{dropped} 行を取りこぼした）");
        }

        if (emitPending) EmitPending(text);

        if (text.Length > 0) WriteOut(text.ToString());
    }

    private void Process(Entry entry, StringBuilder text)
    {
        // 鍵の無い行は、間引かない。順序が入れ替わらないよう、先に溜めた要約を書く
        if (entry.Key is null)
        {
            EmitPending(text);
            AppendLine(text, entry.Time, entry.Message);
            return;
        }

        if (!_runs.TryGetValue(entry.Key, out var run))
        {
            // 流れの最初の1行は、そのまま書く
            _runs[entry.Key] = new Run { LastWrite = entry.Time };
            _hasPendingRuns = true;
            AppendLine(text, entry.Time, entry.Message);
            return;
        }

        if (entry.Time - run.LastWrite < _interval)
        {
            run.Suppressed++;
            run.Last = entry;
            _hasPendingRuns = true;
            return;
        }

        // 間隔が空いた。溜めた分があれば、この行に添えて書く
        if (run.Last is { } last && entry.Time - last.Time >= _interval)
        {
            // 流れがいったん止まってからの1行。止まる前の最後の値は、先に書く
            AppendSummary(text, run);
            AppendLine(text, entry.Time, entry.Message);
        }
        else if (run.Suppressed > 0)
        {
            AppendLine(text, entry.Time, $"{entry.Message} （この前の同種の行 {run.Suppressed} 回は省略）");
        }
        else
        {
            AppendLine(text, entry.Time, entry.Message);
        }

        run.LastWrite = entry.Time;
        run.Suppressed = 0;
        run.Last = null;
    }

    /// <summary>間引き中の行の要約を、時刻の順に書き、流れを終える。</summary>
    private void EmitPending(StringBuilder text)
    {
        if (_runs.Count == 0)
        {
            _hasPendingRuns = false;
            return;
        }

        foreach (var run in _runs.Values.Where(r => r.Suppressed > 0).OrderBy(r => r.Last!.Value.Time))
        {
            AppendSummary(text, run);
        }

        _runs.Clear();
        _hasPendingRuns = false;
    }

    private static void AppendSummary(StringBuilder text, Run run)
    {
        if (run.Last is not { } last || run.Suppressed <= 0) return;

        AppendLine(text, last.Time, $"{last.Message} （同種の行を {run.Suppressed} 回省略。これが最後の値）");
        run.Suppressed = 0;
        run.Last = null;
    }

    private static void AppendLine(StringBuilder text, DateTimeOffset time, string message) =>
        text.Append(time.ToString("O")).Append(' ').Append(message).Append('\n');

    /// <summary>1回の追記。世代の送りもここ。<c>_ioGate</c> を持って呼ぶ。</summary>
    private void WriteOut(string text)
    {
        try
        {
            var path = _path ??= _pathProvider();

            // フォルダは最初の1回だけ確かめる。外から消されたときは、失敗の側で印を戻して作り直す
            if (!_directoryReady)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _directoryReady = true;
            }

            // 大きさはファイルから毎回は読まない（検査が走る）。最初の1回だけ読んで、足した分を数える
            if (_size < 0) _size = File.Exists(path) ? new FileInfo(path).Length : 0;

            if (_size >= _maxBytes) Rotate(path);

            // 送れなくて育ち続けるのを防ぐ（ほかのソフトが握っているなど）。この間の行は諦める
            if (_size >= _maxBytes * 4) return;

            var bytes = new UTF8Encoding(false).GetBytes(text);

            // 開いたままにせず、1回ごとに閉じる。利用者がメモ帳で開いても、消しても邪魔にならない
            using (var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            _size += bytes.Length;
        }
        catch
        {
            // 次に書くときに、フォルダも大きさも確かめ直す
            _directoryReady = false;
            _size = -1;
        }
    }

    /// <summary>
    /// 世代を1つずつ送る（<c>shell.log</c> → <c>.1</c> → <c>.2</c>…）。いちばん古いものは捨てる。
    /// 送れなかったら、そのまま書き足し続ける（上限の4倍で諦める）。
    /// </summary>
    private void Rotate(string path)
    {
        try
        {
            if (_generations > 1)
            {
                var oldest = $"{path}.{_generations - 1}";
                if (File.Exists(oldest)) File.Delete(oldest);

                for (var i = _generations - 1; i >= 2; i--)
                {
                    var from = $"{path}.{i - 1}";
                    if (File.Exists(from)) File.Move(from, $"{path}.{i}");
                }

                File.Move(path, $"{path}.1");
            }
            else
            {
                File.Delete(path);
            }

            _size = 0;
        }
        catch
        {
            // 送れなかった。_size は上限以上のまま残るので、次のとき、もう一度試す
        }
    }

    private readonly record struct Entry(DateTimeOffset Time, string Message, string? Key);

    /// <summary>同じ鍵の行の流れ。</summary>
    private sealed class Run
    {
        /// <summary>最後に実際に書いた行の時刻。</summary>
        public DateTimeOffset LastWrite;

        /// <summary>書かずに省いた行の数。</summary>
        public int Suppressed;

        /// <summary>省いた行のうち、最後のもの。</summary>
        public Entry? Last;
    }
}
