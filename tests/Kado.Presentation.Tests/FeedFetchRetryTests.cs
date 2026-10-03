using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Kado.Presentation;
using Kado.Presentation.Net;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 実働日の自動取得が失敗したときのやり直し方。
/// <para>
/// スリープから戻った直後はネットワークが無い。そこで「今日は取りに行った」印を書くと、その日は二度と
/// 取りに行かない。逆に、時間切れで印が書かれず1分ごとに叩き続けるのも良くない。一時的な失敗は印を書かず、
/// 5分→15分→1時間と間隔を延ばしながらその日のうちにやり直す。404 や中身が読めない失敗は、印を書いて翌日に回す。
/// </para>
/// </summary>
public sealed class FeedFetchRetryTests
{
    private const string FeedUrl = "https://example.com/feed.json";

    private const string Feed = """
        {"app":"inaCalendar","type":"workingdays","updatedAt":"2026-09-20",
         "workingDays":["2026-09-24"],
         "dataStart":"2026-09-24","dataEnd":"2026-09-24"}
        """;

    /// <summary>呼ばれるたびに、決めた振る舞いを順に返す。足りなくなったら最後のものを繰り返す。</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] behaviors) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(behaviors[Math.Min(Calls - 1, behaviors.Length - 1)]());
        }
    }

    private static Func<HttpResponseMessage> Ok() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(Feed, Encoding.UTF8, "application/json"),
    };

    private static Func<HttpResponseMessage> Status(HttpStatusCode status, string body = "") => () => new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    /// <summary>接続できない（スリープ明けでネットワークが無い）。</summary>
    private static Func<HttpResponseMessage> NoNetwork() =>
        () => throw new HttpRequestException("送信できません", new SocketException((int)SocketError.NetworkUnreachable));

    private static Func<HttpResponseMessage> TimedOut() =>
        () => throw new TaskCanceledException("timeout", new TimeoutException());

    private sealed record Rig(
        MainViewModel Vm, ScriptedHandler Handler, AppSettings Settings, List<string> Lines, FakeTimeProvider Clock);

    private static Rig Create(TestWorkspace test, DateTime start, params Func<HttpResponseMessage>[] behaviors)
    {
        var settings = new AppSettings(test.Workspace.Settings) { FeedUrl = FeedUrl, FeedAuto = true };

        var handler = new ScriptedHandler(behaviors);
        var lines = new List<string>();
        var feed = new WorkdayFeedClient(new HttpClient(handler), new NetworkLog(lines.Add));

        // 取りに行く時刻は、時計から読む。試験では時計を固定する
        var clock = new FakeTimeProvider(new DateTimeOffset(start, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);

        var vm = new MainViewModel(
            test.Workspace, DateOnly.FromDateTime(start), settings: settings, feed: feed, clock: clock);

        return new Rig(vm, handler, settings, lines, clock);
    }

    /// <summary>通信が済んで、判断（印を書く／間隔を延ばす）まで行われるのを待つ。</summary>
    private static async Task WaitForCallsAsync(ScriptedHandler handler, int calls, List<string> lines, int decisions)
    {
        for (var i = 0; i < 400 && (handler.Calls < calls || lines.Count(IsDecision) < decisions); i++)
        {
            await Task.Delay(5);
        }

        // 札を下ろすのは判断の少しあと。次の確認が札に弾かれないよう、ひと呼吸おく
        await Task.Delay(20);
    }

    private static bool IsDecision(string line) =>
        line.Contains("取得済みの印", StringComparison.Ordinal);

    private static async Task SettleAsync() => await Task.Delay(60);

    private static readonly DateTime Morning = new(2026, 9, 24, 9, 0, 0);

    // ------------------------------------------------------------------
    // 一時的な失敗: 印を書かず、間隔を延ばしてやり直す
    // ------------------------------------------------------------------

    [Fact]
    public async Task 接続できなければ印を書かず5分後にやり直す()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, NoNetwork(), Ok());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        // 印は書かない。その日のうちにやり直せる
        Assert.Null(rig.Settings.FeedCheckedOn);

        // 1分ごとの確認では、通信しない
        rig.Vm.UpdateNow(Morning.AddMinutes(1));
        rig.Vm.UpdateNow(Morning.AddMinutes(4));
        await SettleAsync();
        Assert.Equal(1, rig.Handler.Calls);

        // 5分たった。ネットワークが戻っている
        rig.Vm.UpdateNow(Morning.AddMinutes(5));
        await WaitForCallsAsync(rig.Handler, 2, rig.Lines, 1);

        Assert.Equal(2, rig.Handler.Calls);
        Assert.Equal(new DateOnly(2026, 9, 24), rig.Settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 失敗が続くと5分_15分_1時間と間隔を延ばす()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, NoNetwork());

        // 1回目（起動時）
        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        // 2回目は5分後
        rig.Vm.UpdateNow(Morning.AddMinutes(5));
        await WaitForCallsAsync(rig.Handler, 2, rig.Lines, 2);

        // 3回目は、その15分後（9:20）。それより前には行かない
        rig.Vm.UpdateNow(Morning.AddMinutes(19));
        await SettleAsync();
        Assert.Equal(2, rig.Handler.Calls);

        rig.Vm.UpdateNow(Morning.AddMinutes(20));
        await WaitForCallsAsync(rig.Handler, 3, rig.Lines, 3);

        // 4回目は、その1時間後（10:20）
        rig.Vm.UpdateNow(Morning.AddMinutes(79));
        await SettleAsync();
        Assert.Equal(3, rig.Handler.Calls);

        rig.Vm.UpdateNow(Morning.AddMinutes(80));
        await WaitForCallsAsync(rig.Handler, 4, rig.Lines, 4);

        // 5回目も1時間後（延ばしきったら、そのまま）
        rig.Vm.UpdateNow(Morning.AddMinutes(139));
        await SettleAsync();
        Assert.Equal(4, rig.Handler.Calls);

        rig.Vm.UpdateNow(Morning.AddMinutes(140));
        await WaitForCallsAsync(rig.Handler, 5, rig.Lines, 5);

        Assert.Equal(5, rig.Handler.Calls);
        Assert.Null(rig.Settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 時間切れも一時的な失敗として1分ごとに叩かない()
    {
        // 以前は、時間切れ（TaskCanceledException）で印が書かれず、1分ごとに取りに行き続けた
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, TimedOut());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        for (var minute = 1; minute < 5; minute++) rig.Vm.UpdateNow(Morning.AddMinutes(minute));
        await SettleAsync();

        Assert.Equal(1, rig.Handler.Calls);
        Assert.Null(rig.Settings.FeedCheckedOn);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    public async Task サーバーの不調やプロキシの断りは印を書かない(HttpStatusCode status)
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, Status(status));

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        Assert.Null(rig.Settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 日が変わったら間隔の持ち越しなしですぐ取りに行く()
    {
        using var test = TestWorkspace.Create();
        var night = new DateTime(2026, 9, 24, 23, 0, 0);
        var rig = Create(test, night, NoNetwork(), NoNetwork(), NoNetwork(), Ok());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);
        rig.Vm.UpdateNow(night.AddMinutes(5));
        await WaitForCallsAsync(rig.Handler, 2, rig.Lines, 2);
        rig.Vm.UpdateNow(night.AddMinutes(20));
        await WaitForCallsAsync(rig.Handler, 3, rig.Lines, 3);

        // 次は1時間後（0:20）のはずだが、日が変わった。0:01 でも新しい日の分を取りに行く
        rig.Vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));
        await WaitForCallsAsync(rig.Handler, 4, rig.Lines, 3);

        Assert.Equal(4, rig.Handler.Calls);
        await WaitUntilAsync(() => rig.Settings.FeedCheckedOn == new DateOnly(2026, 9, 25));
        Assert.Equal(new DateOnly(2026, 9, 25), rig.Settings.FeedCheckedOn);
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++) await Task.Delay(5);
    }

    // ------------------------------------------------------------------
    // 恒久的な失敗: 印を書いて翌日に回す
    // ------------------------------------------------------------------

    [Fact]
    public async Task 見つからなければ印を書いて翌日に回す()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, Status(HttpStatusCode.NotFound, "<html>404</html>"), Ok());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        // 待っても直らない。印を書いて、今日はもう取りに行かない
        Assert.Equal(new DateOnly(2026, 9, 24), rig.Settings.FeedCheckedOn);

        rig.Vm.UpdateNow(Morning.AddMinutes(5));
        rig.Vm.UpdateNow(Morning.AddHours(2));
        await SettleAsync();
        Assert.Equal(1, rig.Handler.Calls);

        // 翌日は、また取りに行く
        rig.Vm.UpdateNow(new DateTime(2026, 9, 25, 0, 1, 0));
        await WaitForCallsAsync(rig.Handler, 2, rig.Lines, 1);

        Assert.Equal(2, rig.Handler.Calls);
        await WaitUntilAsync(() => rig.Settings.FeedCheckedOn == new DateOnly(2026, 9, 25));
        Assert.Equal(new DateOnly(2026, 9, 25), rig.Settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 中身が読めなければ印を書いて翌日に回す()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("これはJSONではない", Encoding.UTF8, "application/json"),
        });

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        Assert.Equal(new DateOnly(2026, 9, 24), rig.Settings.FeedCheckedOn);
    }

    [Fact]
    public async Task 取れたら印を書き失敗の数えを戻す()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, NoNetwork(), Ok());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);
        rig.Vm.UpdateNow(Morning.AddMinutes(5));

        await WaitUntilAsync(() => rig.Settings.FeedCheckedOn == new DateOnly(2026, 9, 24));
        Assert.Equal(new DateOnly(2026, 9, 24), rig.Settings.FeedCheckedOn);

        // 取れたあとは、その日はもう行かない
        rig.Vm.UpdateNow(Morning.AddHours(3));
        await SettleAsync();
        Assert.Equal(2, rig.Handler.Calls);
    }

    // ------------------------------------------------------------------
    // 記録
    // ------------------------------------------------------------------

    [Fact]
    public async Task 失敗と次の手を1行ずつ記録する()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, NoNetwork());

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        Assert.Contains(rig.Lines, l => l.StartsWith("実働日の配信: 取得で失敗。", StringComparison.Ordinal) && l.Contains("SocketException", StringComparison.Ordinal));
        Assert.Contains(rig.Lines, l => l.Contains("5分後にやり直す", StringComparison.Ordinal) && l.Contains("連続1回目", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 恒久的な失敗はその旨と応答の要点を記録する()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, Status(HttpStatusCode.NotFound, "<html>404</html>"));

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        Assert.Contains(rig.Lines, l => l.Contains("応答 404 NotFound", StringComparison.Ordinal) && l.Contains("Content-Type=text/html", StringComparison.Ordinal));
        Assert.Contains(rig.Lines, l => l.Contains("恒久的な失敗", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Lines, l => l.Contains("<html>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task プロキシの認証の断りは認証方式まで記録する()
    {
        using var test = TestWorkspace.Create();
        var rig = Create(test, Morning, () => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
        {
            Headers = { ProxyAuthenticate = { new AuthenticationHeaderValue("NTLM") } },
        });

        await WaitForCallsAsync(rig.Handler, 1, rig.Lines, 1);

        Assert.Contains(rig.Lines, l => l.Contains("407", StringComparison.Ordinal) && l.Contains("Proxy-Authenticate=NTLM", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 判断そのもの
    // ------------------------------------------------------------------

    [Fact]
    public void 間隔は5分_15分_1時間で頭打ち()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), FeedFetchPolicy.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(15), FeedFetchPolicy.RetryDelay(2));
        Assert.Equal(TimeSpan.FromHours(1), FeedFetchPolicy.RetryDelay(3));
        Assert.Equal(TimeSpan.FromHours(1), FeedFetchPolicy.RetryDelay(4));
        Assert.Equal(TimeSpan.FromHours(1), FeedFetchPolicy.RetryDelay(50));
        Assert.Equal(TimeSpan.FromMinutes(5), FeedFetchPolicy.RetryDelay(0));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, true)]
    public void 状態コードで一時的かを決める(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, FeedFetchPolicy.IsTransient(new HttpRequestException("x", null, status)));

    [Fact]
    public void 通信の失敗は一時的で中身や設定の失敗は恒久的()
    {
        Assert.True(FeedFetchPolicy.IsTransient(new HttpRequestException("x", new SocketException((int)SocketError.HostUnreachable))));
        Assert.True(FeedFetchPolicy.IsTransient(new TaskCanceledException("t", new TimeoutException())));
        Assert.True(FeedFetchPolicy.IsTransient(new HttpRequestException("途中で切れた")));
        Assert.True(FeedFetchPolicy.IsTransient(new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("y"))));

        Assert.False(FeedFetchPolicy.IsTransient(new InvalidDataException("実働日データとして読めませんでした")));
        Assert.False(FeedFetchPolicy.IsTransient(new System.Text.Json.JsonException("j")));
        Assert.False(FeedFetchPolicy.IsTransient(new InvalidOperationException("配信元は https:// で始まる URL にしてください。")));
        Assert.False(FeedFetchPolicy.IsTransient(new ArgumentException("x")));
    }

    [Fact]
    public void 手で押した取得の失敗は原因ごとの文言にして型名を出さない()
    {
        Assert.Contains("プロキシの認証", FeedFetchPolicy.Describe(new HttpRequestException("x", null, HttpStatusCode.ProxyAuthenticationRequired)), StringComparison.Ordinal);
        Assert.Contains("404", FeedFetchPolicy.Describe(new HttpRequestException("x", null, HttpStatusCode.NotFound)), StringComparison.Ordinal);
        Assert.Contains("403", FeedFetchPolicy.Describe(new HttpRequestException("x", null, HttpStatusCode.Forbidden)), StringComparison.Ordinal);
        Assert.Contains("接続できません", FeedFetchPolicy.Describe(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))), StringComparison.Ordinal);
        Assert.Contains("時間内に応答がありませんでした", FeedFetchPolicy.Describe(new TaskCanceledException("t")), StringComparison.Ordinal);
        Assert.Contains("証明書", FeedFetchPolicy.Describe(new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("y"))), StringComparison.Ordinal);

        // Kado が日本語で書いた文はそのまま。英語の .NET のメッセージは出さない
        Assert.Equal("配信元のファイルが大きすぎます。", FeedFetchPolicy.Describe(new InvalidDataException("配信元のファイルが大きすぎます。")));

        var unknown = FeedFetchPolicy.Describe(new InvalidOperationException("Sequence contains no elements"));
        Assert.DoesNotContain("Sequence", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", unknown, StringComparison.Ordinal);
    }
}
