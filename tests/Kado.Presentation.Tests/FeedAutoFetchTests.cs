using System.Net;
using System.Text;
using Kado.Presentation;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 配信元から実働日データを自動で取りに行く条件。
/// <para>
/// 以前は起動したときにしか見ていなかった。閉じるボタンでトレイに入る作りが既定で、
/// ログオン時に自動で起動もするので、何日も立ち上げっぱなしになる。そのあいだ
/// 会社の実働日カレンダーが更新されても古いままだった。
/// </para>
/// </summary>
public sealed class FeedAutoFetchTests
{
    private const string FeedUrl = "https://example.com/feed.json";

    /// <summary>取りに来た回数を数えるだけの相手。中身は最小限の実働日データ。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

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

    private static (MainViewModel Vm, CountingHandler Handler, AppSettings Settings) Create(
        TestWorkspace test, DateOnly today, bool auto = true, DateOnly? checkedOn = null)
    {
        var settings = new AppSettings(test.Workspace.Settings)
        {
            FeedUrl = FeedUrl,
            FeedAuto = auto,
            FeedCheckedOn = checkedOn,
        };

        var handler = new CountingHandler();
        var feed = new WorkdayFeedClient(new HttpClient(handler));

        return (new MainViewModel(test.Workspace, today, settings: settings, feed: feed), handler, settings);
    }

    [Fact]
    public async Task 起動したその日にまだなら取りに行く()
    {
        using var test = TestWorkspace.Create();
        var (_, handler, _) = Create(test, new DateOnly(2026, 9, 24));

        await WaitForAsync(() => handler.Calls >= 1);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task 日付をまたいだら取りに行く()
    {
        using var test = TestWorkspace.Create();

        // 起動した日はもう取りに行ったことにしておく
        var (vm, handler, settings) = Create(
            test, new DateOnly(2026, 9, 24), checkedOn: new DateOnly(2026, 9, 24));

        vm.UpdateNow(new DateTime(2026, 9, 24, 23, 59, 0));
        Assert.Equal(0, handler.Calls);

        // 日付が変わった。立ち上げ直さなくても取りに行く
        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));

        await WaitForAsync(() => handler.Calls >= 1);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new DateOnly(2026, 9, 25), settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 同じ日に何度呼ばれても一度だけ()
    {
        using var test = TestWorkspace.Create();
        var (vm, handler, _) = Create(
            test, new DateOnly(2026, 9, 24), checkedOn: new DateOnly(2026, 9, 24));

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));
        await WaitForAsync(() => handler.Calls >= 1);

        for (var minute = 2; minute < 10; minute++)
        {
            vm.UpdateNow(new DateTime(2026, 9, 25, 0, minute, 0));
        }

        await Task.Delay(50);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task 自動を切っていれば取りに行かない()
    {
        using var test = TestWorkspace.Create();
        var (vm, handler, _) = Create(
            test, new DateOnly(2026, 9, 24), auto: false, checkedOn: new DateOnly(2026, 9, 24));

        vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));
        await Task.Delay(50);

        Assert.Equal(0, handler.Calls);
    }

    /// <summary>取りに行くのは待たない作りなので、済むまで少しだけ待つ。</summary>
    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(10);
    }
}
