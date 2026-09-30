using System.Net;
using System.Text;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 実働日の配信の取得は、画面のスレッドで通信を始めない。
/// <para>
/// 起動時の取得は <c>MainViewModel</c> のコンストラクタから始まる。最初の要求では、経路
/// （プロキシ）の自動検出が呼んだスレッドのまま数秒止まることがある（会社の回線）。
/// 画面のスレッドで始めると、その間ウィンドウが出ない。
/// </para>
/// </summary>
public sealed class FeedFetchThreadTests
{
    private sealed class ThreadRecordingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<int> _thread = new();

        /// <summary>要求が届いたスレッド。</summary>
        public Task<int> Thread => _thread.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _thread.TrySetResult(Environment.CurrentManagedThreadId);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"app":"inaCalendar","type":"workingdays","updatedAt":"2026-09-20",
                     "workingDays":["2026-09-24"],
                     "dataStart":"2026-09-24","dataEnd":"2026-09-24"}
                    """,
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task 起動時の取得は呼び出したスレッドで通信を始めない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings)
        {
            FeedUrl = "https://example.com/feed.json",
            FeedAuto = true,
            FeedCheckedOn = null,
        };

        var handler = new ThreadRecordingHandler();
        var feed = new WorkdayFeedClient(new HttpClient(handler));

        // 呼び出し側は、スレッドプールに属さない専用のスレッドにする。プールのスレッドだと、
        // 呼び出しが終わったあとに同じスレッドが通信を拾っても「同じ」に見えてしまう
        var callerThread = 0;
        Exception? failure = null;

        var caller = new Thread(() =>
        {
            try
            {
                callerThread = Environment.CurrentManagedThreadId;

                // コンストラクタの中から取得が始まる
                _ = new MainViewModel(
                    test.Workspace, new DateOnly(2026, 9, 24), settings: settings, feed: feed);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        caller.Start();
        caller.Join();

        Assert.Null(failure);

        var sentOn = await handler.Thread.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerThread, sentOn);

        // 取り込みまで済んでから、ワークスペースを閉じる
        for (var i = 0; i < 200 && settings.FeedCheckedOn is null; i++) await Task.Delay(10);
    }
}
