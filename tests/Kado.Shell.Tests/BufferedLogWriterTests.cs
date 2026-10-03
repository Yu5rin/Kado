using System.Text;
using Kado.App.Shell;

namespace Kado.Shell.Tests;

/// <summary>
/// shell.log の書き手。画面のスレッドでは書かず、まとめて書く。
/// 高頻度の行は間引き、切り分けに要る行は1行も落とさない。
/// </summary>
public sealed class BufferedLogWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kado-shelllog-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private string LogPath => Path.Combine(_dir, "shell.log");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>書き手のスレッドを起こさず、Flush でだけ書く形。時刻は手で進める。</summary>
    private BufferedLogWriter Create(long maxBytes = 1024 * 1024, int generations = 3) =>
        new(() => LogPath, maxBytes, generations, TimeSpan.FromSeconds(1), () => _now, useThread: false);

    private string[] Lines(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    [Fact]
    public void 積んだだけではファイルに触れず_Flushで書かれる()
    {
        var writer = Create();

        writer.Enqueue("startup-timing OnStartup=10ms");

        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(LogPath));

        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Single(lines);
        Assert.EndsWith(" startup-timing OnStartup=10ms", lines[0]);
        Assert.StartsWith(_now.ToString("O"), lines[0]);
    }

    [Fact]
    public void 書き込みは呼んだスレッドの外で行われ_終了時のFlushで取りこぼさない()
    {
        // 書き手のスレッドを起こす本物の形。Flush を呼べば、スレッドの都合に関わらず全部が書かれている
        using var writer = new BufferedLogWriter(() => LogPath);

        for (var i = 0; i < 500; i++) writer.Enqueue($"line {i}");

        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Equal(500, lines.Length);
        Assert.EndsWith(" line 0", lines[0]);
        Assert.EndsWith(" line 499", lines[^1]);
    }

    [Fact]
    public void 別のスレッドからの積み込みでも_行は落ちず並びも保たれる()
    {
        using var writer = new BufferedLogWriter(() => LogPath);

        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (var i = 0; i < 200; i++) writer.Enqueue($"t{t} n{i}");
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Equal(800, lines.Length);

        // 同じスレッドが積んだ行は、積んだ順に並ぶ
        for (var t = 0; t < 4; t++)
        {
            var mine = lines.Where(l => l.Contains($" t{t} n"))
                .Select(l => int.Parse(l[(l.LastIndexOf('n') + 1)..])).ToList();
            Assert.Equal(Enumerable.Range(0, 200), mine);
        }
    }

    [Fact]
    public void 鍵のない行は_何行続いても1行も間引かない()
    {
        var writer = Create();

        for (var i = 0; i < 100; i++) writer.Enqueue($"google 同期 {i}");

        writer.Flush();

        Assert.Equal(100, Lines(LogPath).Length);
    }

    [Fact]
    public void 同じ鍵の行は_間隔のあいだに1行だけ書き_最後の値を要約で残す()
    {
        var writer = Create();

        for (var i = 0; i < 50; i++)
        {
            writer.Enqueue($"WM_WINDOWPOSCHANGED cx={400 + i}", "pos");
            _now = _now.AddMilliseconds(10);
        }

        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" WM_WINDOWPOSCHANGED cx=400", lines[0]);
        Assert.Contains("WM_WINDOWPOSCHANGED cx=449", lines[1]);
        Assert.Contains("49 回省略", lines[1]);
        Assert.Contains("最後の値", lines[1]);
    }

    [Fact]
    public void 間隔が空けば_同じ鍵でも新しい1行を書く()
    {
        var writer = Create();

        writer.Enqueue("A cx=1", "pos");
        _now = _now.AddMilliseconds(100);
        writer.Enqueue("A cx=2", "pos");
        _now = _now.AddMilliseconds(100);
        writer.Enqueue("A cx=3", "pos");

        // 1秒たつと、溜めた分を添えて書く
        _now = _now.AddSeconds(2);
        writer.Enqueue("A cx=4", "pos");
        writer.Flush();

        var lines = Lines(LogPath);

        // 流れが止まっていたので、止まる前の最後の値（cx=3）が先に要約され、そのあと cx=4 が書かれる
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" A cx=1", lines[0]);
        Assert.Contains("A cx=3", lines[1]);
        Assert.Contains("2 回省略", lines[1]);
        Assert.EndsWith(" A cx=4", lines[2]);
    }

    [Fact]
    public void 鍵のない行が割り込んでも_行の順序は入れ替わらず要約も失われない()
    {
        var writer = Create();

        writer.Enqueue("pos cx=1", "pos");
        writer.Enqueue("pos cx=2", "pos");
        writer.Enqueue("pos cx=3", "pos");
        writer.Enqueue("update 確認 失敗 理由=proxy");
        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" pos cx=1", lines[0]);
        Assert.Contains("pos cx=3", lines[1]);
        Assert.Contains("2 回省略", lines[1]);
        Assert.EndsWith(" update 確認 失敗 理由=proxy", lines[2]);
    }

    [Fact]
    public void 鍵が違えば_別々の流れとして数える()
    {
        var writer = Create();

        writer.Enqueue("a 1", "a");
        writer.Enqueue("b 1", "b");
        writer.Enqueue("a 2", "a");
        writer.Enqueue("b 2", "b");
        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Equal(4, lines.Length);
        Assert.Contains(lines, l => l.Contains("a 2") && l.Contains("1 回省略"));
        Assert.Contains(lines, l => l.Contains("b 2") && l.Contains("1 回省略"));
    }

    [Fact]
    public void 上限を超えたら世代を送り_3世代まで残す()
    {
        // 1行が約 60 バイト。400 バイトで送る
        var writer = Create(maxBytes: 400, generations: 3);

        for (var i = 0; i < 40; i++)
        {
            writer.Enqueue($"line {i:D3} " + new string('x', 20));
            writer.Flush();
        }

        Assert.True(File.Exists(LogPath));
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.True(File.Exists(LogPath + ".2"));
        Assert.False(File.Exists(LogPath + ".3"));

        // 新しい行ほど現行に近く、最後の行は現行にある
        Assert.Contains(" line 039 ", Lines(LogPath)[^1]);

        // 世代をまたいでも、並びの抜けは古い側にしか出ない
        var all = Lines(LogPath + ".2").Concat(Lines(LogPath + ".1")).Concat(Lines(LogPath))
            .Select(l => int.Parse(l.Split(' ')[2])).ToList();
        Assert.Equal(all.OrderBy(x => x), all);
        Assert.Equal(39, all[^1]);
    }

    [Fact]
    public void 既定は1MBを3世代で_幅つまみの数十秒では起動の記録が消えない()
    {
        var writer = Create();

        writer.Enqueue("startup-timing OnStartup=10ms 合計=900ms");

        // つまんでいるあいだに来る通知を10万回。間引きで数行になる
        for (var i = 0; i < 100_000; i++)
        {
            writer.Enqueue($"OnMessage WM_WINDOWPOSCHANGING x=0 y=0 cx={400 + i % 300} cy=900", "pos");
            _now = _now.AddMilliseconds(1);
        }

        writer.Flush();

        var lines = Lines(LogPath);
        Assert.Contains(lines, l => l.Contains("startup-timing"));
        Assert.True(lines.Length < 400, $"行数 {lines.Length}");
        Assert.False(File.Exists(LogPath + ".1"));
    }

    [Fact]
    public void 現行ファイルが残っていれば_大きさを引き継いで続きから書く()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, new string('x', 390) + "\n");

        var writer = Create(maxBytes: 400, generations: 3);
        writer.Enqueue("次の行 " + new string('y', 30));
        writer.Flush();
        writer.Enqueue("さらに次");
        writer.Flush();

        // 最初の追記は上限未満のファイルに足され、次の追記の前に世代が送られる
        Assert.Contains("xxxx", File.ReadAllText(LogPath + ".1"));
        Assert.Contains("次の行", File.ReadAllText(LogPath + ".1"));
        Assert.Single(Lines(LogPath));
    }

    [Fact]
    public void 保存先のフォルダが消されても_次の書き込みで作り直す()
    {
        var writer = Create();

        writer.Enqueue("1行目");
        writer.Flush();

        Directory.Delete(_dir, recursive: true);

        writer.Enqueue("2行目");
        writer.Flush();   // フォルダが無く失敗する（この行は諦めてよい）

        writer.Enqueue("3行目");
        writer.Flush();

        Assert.Contains(Lines(LogPath), l => l.EndsWith(" 3行目"));
    }

    [Fact]
    public void 書き込みに失敗しても例外は投げない()
    {
        // 保存先の「フォルダ」が、ファイルに化けている
        var blocker = Path.Combine(Path.GetTempPath(), "kado-shelllog-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");

        try
        {
            var writer = new BufferedLogWriter(
                () => Path.Combine(blocker, "shell.log"), useThread: false);

            var ex = Record.Exception(() =>
            {
                writer.Enqueue("書けない");
                writer.Flush();
            });

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ファイルは開いたままにしない_書いたあとでも消せる()
    {
        var writer = Create();

        writer.Enqueue("1行");
        writer.Flush();

        File.Delete(LogPath);

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Dispose後の積み込みは何も起こさない()
    {
        var writer = Create();
        writer.Dispose();

        writer.Enqueue("終わったあと");
        writer.Flush();

        Assert.False(File.Exists(LogPath));
    }
}
