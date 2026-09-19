using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace SlideinaCalendar.App;

/// <summary>
/// 二重起動を防ぐ。
/// <para>
/// <b>2本動くと同期が壊れる。</b>どちらも同じデータベースを開き、同じカレンダーへ
/// 書き戻す。片方が入れた予定をもう片方が知らないまま送り、差分の印も食い違う。
/// </para>
/// <para>
/// 2本目は、すでに動いているほうに「前に出ろ」と伝えてから静かに終わる。
/// 黙って終わると、押したのに何も起きないように見える。
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// 合図に使う名前。
    /// <para>
    /// <c>Local\</c> なので、同じ Windows ユーザーのセッション内だけで見える。
    /// 共有 PC で別の人が使っていても、こちらの起動は妨げない。
    /// </para>
    /// </summary>
    private const string MutexName = @"Local\SlideinaCalendar.SingleInstance";

    private const string PipeName = "SlideinaCalendar.Activate";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _listening = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// 最初の1本として起動できたか試す。
    /// <para>取れなければ、すでに動いている。</para>
    /// </summary>
    /// <param name="wait">
    /// どれだけ待つか。
    /// <para>
    /// ふつうは待たない。<b>更新で入れ替えた直後だけ待つ。</b>前のプロセスがまだ
    /// 終わりきっておらず、待たずに判定すると「すでに起動しています」で即座に終わり、
    /// 更新したのに立ち上がらないように見える。
    /// </para>
    /// </param>
    /// <returns>取れたら実体。取れなければ null。</returns>
    public static SingleInstance? TryAcquire(TimeSpan wait = default)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out _);

        try
        {
            if (mutex.WaitOne(wait <= TimeSpan.Zero ? TimeSpan.Zero : wait))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // 前のプロセスが返さずに落ちた。こちらが引き継ぐ
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// すでに動いているほうに、前に出るよう伝える。
    /// <para>届かなくても構わない。相手が終わりかけているだけのこともある。</para>
    /// </summary>
    public static void AskRunningInstanceToShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);

            // 相手が応じなければ諦める。待たせるより、こちらが静かに終わるほうがよい
            client.Connect(timeout: 1500);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // 伝えられなくても、2本目を起動しないことのほうが大事
        }
    }

    /// <summary>
    /// 「前に出ろ」の合図を待ち受ける。
    /// <para>受け取ったら <paramref name="onActivate"/> を呼ぶ。</para>
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);

        _ = Task.Run(async () =>
        {
            while (!_listening.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);

                    await server.WaitForConnectionAsync(_listening.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync(_listening.Token).ConfigureAwait(false) is not null)
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // 相手が途中で切れた。待ち受けは続ける
                }
            }
        }, _listening.Token);
    }

    public void Dispose()
    {
        _listening.Cancel();
        _listening.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 取っていない状態で返そうとした。終了処理なので問題にしない
        }

        _mutex.Dispose();
    }
}
