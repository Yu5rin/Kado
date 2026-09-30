using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kado.Presentation.Infrastructure;

/// <summary>
/// 起動の記録（切り分け用）。
/// <para>
/// 実機で「起動から最初の描画まで約14秒」となったが、静的に読んでも止まっている場所が
/// 確定しなかった。次に同じことが起きたとき、ログだけで区間を切り分けられるよう、
/// 2種類の行を <c>shell.log</c> へ残す。
/// </para>
/// <list type="bullet">
/// <item>
/// <b><c>startup-timing</c>（1回の起動で1行）</b>――印（<see cref="Mark"/>）を付けた各点の
/// 累計ms と、直前の印からの区間ms。最初の描画（<c>ContentRendered</c>）で
/// <see cref="Finish"/> がまとめて書く。
/// </item>
/// <item>
/// <b><c>startup-slow</c>（1件1行）</b>――起動中に <see cref="Measure(string, Action)"/> で
/// 包んだ処理が <see cref="SlowThresholdMs"/> を超えたとき、その場で書く。
/// </item>
/// </list>
/// <para>
/// <b>記録は動作に影響させない。</b>書き先（<see cref="Sink"/>）が無ければ何もしない。
/// 最初の描画が済んだあと（<see cref="Finish"/> 以後）は印も測定も止まり、素通りするだけ
/// なので、常用の経路（何度も呼ばれるビューの組み直しなど）に包んでおいても重くならない。
/// <b>個人情報や予定の内容は書かない。名前と ms だけ。</b>
/// </para>
/// </summary>
public sealed class StartupTimeline
{
    /// <summary>これ以上かかった処理は、1件ずつ記録する。</summary>
    public const double SlowThresholdMs = 100;

    private readonly object _gate = new();
    private readonly Func<double> _now;
    private readonly List<(string Name, double At)> _marks = [];
    private bool _finished;

    /// <param name="now">起動からの経過（ms）を返す時計。試験では進められるものを渡す。</param>
    public StartupTimeline(Func<double> now) => _now = now ?? throw new ArgumentNullException(nameof(now));

    /// <summary>書き先（<c>shell.log</c> へ1行書く関数）。null なら何も書かない。</summary>
    public Action<string>? Sink { get; set; }

    /// <summary>最初の描画まで済んだ（もう記録しない）か。</summary>
    public bool IsFinished
    {
        get { lock (_gate) return _finished; }
    }

    /// <summary>起動からの経過（ms）。</summary>
    public double ElapsedMs => _now();

    /// <summary>いまの時点に名前を付けて控える。最後にまとめて1行で書く。</summary>
    public void Mark(string name)
    {
        lock (_gate)
        {
            if (_finished) return;

            _marks.Add((name, _now()));
        }
    }

    /// <summary>
    /// 処理にかかった時間を測る。<see cref="SlowThresholdMs"/> 以上なら1行書く。
    /// <para>例外が出ても、かかった時間は記録してから投げ直す。</para>
    /// </summary>
    public T Measure<T>(string name, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (IsFinished) return work();

        var started = _now();

        try
        {
            return work();
        }
        finally
        {
            ReportIfSlow(name, _now() - started);
        }
    }

    /// <inheritdoc cref="Measure{T}(string, Func{T})"/>
    public void Measure(string name, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Measure<object?>(name, () =>
        {
            work();
            return null;
        });
    }

    /// <summary>
    /// <c>using</c> で包んだ区間の時間を測る。<see cref="Measure(string, Action)"/> と同じ扱い。
    /// <para>
    /// 代入の流れを型に教えている（<c>MemberNotNull</c>）メソッドなど、ラムダに包むと
    /// 警告が出る呼び出しに使う。
    /// </para>
    /// </summary>
    public Span Measure(string name) => new(this, name, IsFinished ? 0 : _now());

    /// <summary><see cref="Measure(string)"/> が返す、閉じたときに時間を記録する区間。</summary>
    public readonly struct Span : IDisposable
    {
        private readonly StartupTimeline? _owner;
        private readonly string _name;
        private readonly double _started;

        internal Span(StartupTimeline owner, string name, double started)
        {
            _owner = owner;
            _name = name;
            _started = started;
        }

        public void Dispose()
        {
            if (_owner is { } owner && !owner.IsFinished) owner.ReportIfSlow(_name, owner._now() - _started);
        }
    }

    /// <summary>
    /// 最初の描画。ここまでの印を1行にまとめて書き、以後の記録を止める。
    /// </summary>
    /// <returns>書いた行。すでに終えていれば null。</returns>
    public string? Finish(string lastName = "ContentRendered")
    {
        string line;

        lock (_gate)
        {
            if (_finished) return null;

            _marks.Add((lastName, _now()));
            _finished = true;
            line = Format(_marks);
        }

        Write(line);
        return line;
    }

    /// <summary>
    /// 印を1行にする。<c>名前=累計ms(+区間ms)</c> を <c>→</c> でつなぐ。
    /// <para>最初の印の区間は、プロセスが生まれてからの経過そのもの（累計と同じ）。</para>
    /// </summary>
    public static string Format(IReadOnlyList<(string Name, double At)> marks)
    {
        var text = new StringBuilder("startup-timing");
        var previous = 0.0;

        for (var i = 0; i < marks.Count; i++)
        {
            var (name, at) = marks[i];

            text.Append(i == 0 ? " " : " →")
                .Append(name).Append('=')
                .Append(Ms(at)).Append("ms(+").Append(Ms(at - previous)).Append("ms)");

            previous = at;
        }

        return text.ToString();
    }

    private void ReportIfSlow(string name, double elapsedMs)
    {
        if (elapsedMs < SlowThresholdMs) return;
        if (IsFinished) return;

        Write($"startup-slow {name}={Ms(elapsedMs)}ms");
    }

    private void Write(string line)
    {
        try
        {
            Sink?.Invoke(line);
        }
        catch
        {
            // 記録できなくても、起動は止めない
        }
    }

    private static string Ms(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

/// <summary>
/// 起動の記録の窓口。アプリ全体で1つの <see cref="StartupTimeline"/> を共有する。
/// <para>
/// 起点は<b>プロセスが生まれた時刻</b>（OS が持つ値）。<c>Main</c> は自動生成コードで
/// 手を入れられないので、ここで数える。
/// </para>
/// </summary>
public static class StartupTrace
{
    private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();

    /// <summary>プロセスの誕生から、この型が初めて触られるまでの経過（ms）。</summary>
    private static readonly double OffsetMs = ReadOffsetMs();

    /// <summary>アプリ全体で共有する記録。</summary>
    public static StartupTimeline Timeline { get; } = new(
        () => OffsetMs + Stopwatch.GetElapsedTime(OriginTimestamp).TotalMilliseconds);

    /// <summary>書き先。アプリが <c>shell.log</c> へ書く関数を入れる。</summary>
    public static Action<string>? Sink
    {
        get => Timeline.Sink;
        set => Timeline.Sink = value;
    }

    /// <summary>起動からの経過（ms）。</summary>
    public static double ElapsedMs => Timeline.ElapsedMs;

    /// <inheritdoc cref="StartupTimeline.Mark"/>
    public static void Mark(string name) => Timeline.Mark(name);

    /// <inheritdoc cref="StartupTimeline.Measure{T}(string, Func{T})"/>
    public static T Measure<T>(string name, Func<T> work) => Timeline.Measure(name, work);

    /// <inheritdoc cref="StartupTimeline.Measure(string, Action)"/>
    public static void Measure(string name, Action work) => Timeline.Measure(name, work);

    /// <inheritdoc cref="StartupTimeline.Measure(string)"/>
    public static StartupTimeline.Span Measure(string name) => Timeline.Measure(name);

    /// <inheritdoc cref="StartupTimeline.Finish"/>
    public static string? Finish(string lastName = "ContentRendered") => Timeline.Finish(lastName);

    private static double ReadOffsetMs()
    {
        try
        {
            using var self = Process.GetCurrentProcess();

            return Math.Max(0, (DateTime.Now - self.StartTime).TotalMilliseconds);
        }
        catch
        {
            // 起点が読めなければ、この型を触った時点を起点にする
            return 0;
        }
    }
}
