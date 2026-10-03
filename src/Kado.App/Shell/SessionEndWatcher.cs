using System.Windows.Interop;
using System.Windows.Threading;
using static Kado.App.Shell.NativeMethods;

namespace Kado.App.Shell;

/// <summary>
/// シャットダウン・サインアウトの合図を、窓とは別のスレッドで見張る。
/// <para>
/// WPF は終了の問い合わせ（<c>WM_QUERYENDSESSION</c>）を了承すると自分で終わるので、他のアプリが
/// 取り消したとき（<c>WM_ENDSESSION</c> の wParam=FALSE）には、もう窓も画面のスレッドも無い。
/// この見張りは<b>独立したスレッドと、独立した見えない窓</b>を持ち、問い合わせを受けたら
/// プロセスを生かしておき、結果を待つ（<see cref="SessionEndRules"/>）。
/// </para>
/// </summary>
internal sealed class SessionEndWatcher : IDisposable
{
    private readonly Action<bool> _onResolved;
    private readonly ManualResetEventSlim _ready = new();

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private DispatcherTimer? _timeout;
    private volatile bool _queried;
    private volatile bool _stopped;

    /// <param name="onResolved">
    /// 結果が届いたとき、見張りのスレッドから呼ばれる。引数は wParam（TRUE なら本当に終わる）。
    /// 本当に終わるときは、ここで後片付けを済ませて返す（返ったあと、Windows がプロセスを終わらせる）。
    /// </param>
    internal SessionEndWatcher(Action<bool> onResolved) =>
        _onResolved = onResolved ?? throw new ArgumentNullException(nameof(onResolved));

    /// <summary>終了の問い合わせを受けたか。</summary>
    internal bool QueryEndSessionSeen => _queried;

    internal void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Kado.SessionEndWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 窓ができるまで少しだけ待つ。間に合わなくても、起動を止めない
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // 見えないトップレベル窓（メッセージ専用ではない。WM_ENDSESSION は全トップレベル窓へ送られる）
            _source = new HwndSource(new HwndSourceParameters("Kado.SessionEnd")
            {
                Width = 0,
                Height = 0,
                WindowStyle = unchecked((int)WS_POPUP),
                ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            });

            _source.AddHook(OnMessage);

            // 結果が届かないまま長く待たない
            _timeout = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = SessionEndRules.MaxWait,
            };
            _timeout.Tick += (_, _) => Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 見張れなくても、アプリは動く（取り消されたら常駐が途切れるだけ）
            _ready.Set();
            return;
        }

        _ready.Set();
        Dispatcher.Run();

        _source?.Dispose();
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_QUERYENDSESSION:
                // 了承はそのまま（既定の処理が TRUE を返す）。ここからは結果を待つあいだ、
                // プロセスを終わらせない（WPF はこの問い合わせで自分を終わらせにかかる）
                _queried = true;
                if (_thread is not null) _thread.IsBackground = false;

                _timeout?.Stop();
                _timeout?.Start();
                break;

            case WM_ENDSESSION:
            {
                var ending = wParam != IntPtr.Zero;
                var reaction = SessionEndRules.OnEndSession(ending, _queried);

                if (reaction != SessionEndRules.Reaction.Ignore)
                {
                    try
                    {
                        _onResolved(ending);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // 後片付けの失敗で、見張りごと止めない
                    }
                }

                // 取り消されたので、問い合わせの状態を戻す。終わるときは、返ったあと Windows が終わらせる
                if (!ending)
                {
                    _queried = false;
                    Stop();
                }

                break;
            }
        }

        return IntPtr.Zero;
    }

    private void Stop()
    {
        if (_stopped) return;

        _stopped = true;
        _timeout?.Stop();
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        // 問い合わせのあとなら、結果が届くまで（または上限まで）生かしておく
        if (_queried) return;

        Stop();
    }
}
