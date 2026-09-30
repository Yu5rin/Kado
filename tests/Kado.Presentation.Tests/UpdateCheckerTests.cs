using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Kado.Presentation.Update;

namespace Kado.Presentation.Tests;

/// <summary>
/// 更新の確認の流れ。HTTP は差し替えて、どこへ問い合わせたか・どう失敗したかを見る。
/// <para>
/// 会社の共有回線では、GitHub の API が回数の上限（403）で毎回断られ、更新できなかった。
/// Atom を先に見ること、API が使えなくても組み立てた URL で更新できることを固定する。
/// </para>
/// </summary>
public class UpdateCheckerTests
{
    private const string ApiUrl = "https://api.github.com/repos/Yu5rin/Kado/releases/latest";
    private const string AtomUrl = "https://github.com/Yu5rin/Kado/releases.atom";

    private static readonly Version Current = new(1, 0, 4);

    private const string ReleaseJson = """
        {
          "tag_name": "v1.0.5",
          "html_url": "https://github.com/Yu5rin/Kado/releases/tag/v1.0.5",
          "body": "### 同期を直しました",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "Kado-1.0.5-win-x64.exe",
              "size": 71662439,
              "digest": "sha256:ABCDEF0123456789",
              "browser_download_url": "https://github.com/Yu5rin/Kado/releases/download/v1.0.5/Kado-1.0.5-win-x64.exe"
            }
          ]
        }
        """;

    // ---- Atom を先に見る -------------------------------------------------

    [Fact]
    public async Task Atomで最新版ならAPIには行かない()
    {
        var h = new Routes { [AtomUrl] = _ => Atom("v1.0.4", "v1.0.3") };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal([AtomUrl], h.Requested);   // API（回数の上限がある側）を呼んでいない
    }

    [Fact]
    public async Task Atomで新しい版があればAPIへ詳細を取りに行く()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5", "v1.0.4"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal([AtomUrl, ApiUrl], h.Requested);

        var info = result.Info!;
        Assert.Equal(new Version(1, 0, 5), info.Version);
        Assert.Equal("### 同期を直しました", info.ReleaseNotes);   // 更新の窓に出す変更点
        Assert.Equal("ABCDEF0123456789", info.Sha256);
        Assert.Equal(71662439, info.SizeBytes);
        Assert.False(info.DetailsUnavailable);
    }

    [Theory]
    [InlineData("v1.0.5", "1.0.4", true)]
    [InlineData("v1.0.4", "1.0.4", false)]
    [InlineData("v1.0.3", "1.0.4", false)]    // 配布元を古い版に戻したとき
    [InlineData("v1.0.10", "1.0.9", true)]    // 文字で比べると古く見える
    [InlineData("v1.1.0", "1.0.99", true)]
    public async Task Atomのタグと今の版を数として比べる(string tag, string current, bool newer)
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom(tag),
            [ApiUrl] = _ => Json(ReleaseJson.Replace("v1.0.5", tag).Replace("1.0.5", tag[1..])),
        };

        var result = await h.Checker(current: Version.Parse(current)).CheckAsync();

        Assert.Equal(newer ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task 要求ごとに適切なAcceptを付ける()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        await h.Checker().CheckAsync();

        Assert.Equal("application/atom+xml", h.AcceptOf(AtomUrl));
        Assert.Equal("application/vnd.github+json", h.AcceptOf(ApiUrl));
    }

    // ---- API が上限（403）でも更新できる ----------------------------------

    [Fact]
    public async Task APIが403でも組み立てたURLで更新ありになる()
    {
        var log = new List<string>();
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5", "v1.0.4"),
            [ApiUrl] = _ => Status(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}"""),
        };

        var result = await h.Checker(log: log).CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);

        var info = result.Info!;
        Assert.Equal(new Version(1, 0, 5), info.Version);
        Assert.Equal("v1.0.5", info.TagName);
        Assert.Equal(
            "https://github.com/Yu5rin/Kado/releases/download/v1.0.5/Kado-1.0.5-win-x64.exe", info.DownloadUrl);
        Assert.Null(info.Sha256);                       // 分からないので照合は省く
        Assert.Equal(string.Empty, info.ReleaseNotes);  // 本文も取れない
        Assert.True(info.DetailsUnavailable);           // 窓は「変更点を読めませんでした」を出す
        Assert.Equal("https://github.com/Yu5rin/Kado/releases/tag/v1.0.5", info.ReleaseUrl);

        // 記録：403 と、組み立てて続行したこと
        Assert.Contains(log, l => l.Contains("403", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("組み立てて続行する", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 組み立てたURLは許可された取得先の検査を通る()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Status(HttpStatusCode.Forbidden),
        };

        var info = (await h.Checker().CheckAsync()).Info!;

        // 落としたものをそのまま実行する。UpdateService は取りに行く前に、これを通す
        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(info.DownloadUrl));
        Assert.True(ReleaseFeed.IsAllowedDownloadUrl(info.ReleaseUrl));
    }

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task APIが他の失敗でも組み立てたURLで更新ありになる(HttpStatusCode status)
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Status(status),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.Info!.DetailsUnavailable);
    }

    [Fact]
    public async Task APIがエラーページを200で返しても組み立てたURLで続ける()
    {
        // 会社のプロキシは、止めた通信の代わりに 200 でエラーページを返すことがある
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Html("<html>アクセスがブロックされました</html>"),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.Info!.DetailsUnavailable);
    }

    [Fact]
    public async Task 新しい版があるのにAPIが最新版だと言うときはAPIに従う()
    {
        // Atom はプレリリースも載せるが、API の latest は安定版だけを返す
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.6", "v1.0.4"),
            [ApiUrl] = _ => Json(ReleaseJson.Replace("v1.0.5", "v1.0.4").Replace("1.0.5", "1.0.4")),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    // ---- 確認そのものが失敗するとき ---------------------------------------

    [Fact]
    public async Task 両方が403なら上限の文言で失敗にする()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Status(HttpStatusCode.Forbidden),
            [ApiUrl] = _ => Status(HttpStatusCode.Forbidden),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);

        var failure = result.Failure!;
        Assert.Equal(UpdateFailureKind.RateLimited, failure.Kind);
        Assert.Contains("回数の上限に達していました", failure.Message, StringComparison.Ordinal);

        // 失敗したときも、リリースのページへ行ける
        Assert.Equal("https://github.com/Yu5rin/Kado/releases/latest", failure.ReleasePageUrl);
    }

    [Fact]
    public async Task 状態407ならプロキシの認証だと記録して伝える()
    {
        var log = new List<string>();
        var h = new Routes
        {
            [AtomUrl] = _ => Status(HttpStatusCode.ProxyAuthenticationRequired, "<html>認証が必要</html>", "text/html"),
            [ApiUrl] = _ => Status(HttpStatusCode.ProxyAuthenticationRequired, "<html>認証が必要</html>", "text/html"),
        };

        var result = await h.Checker(log: log).CheckAsync();

        Assert.Equal(UpdateFailureKind.ProxyAuthRequired, result.Failure!.Kind);
        Assert.Contains("プロキシ", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("応答 407 ProxyAuthenticationRequired", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Content-Type=text/html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 例外の連鎖を記録し接続できないと伝える()
    {
        var log = new List<string>();
        var h = new Routes();   // どの URL も、接続を拒否される
        h.ThrowFor = _ => new HttpRequestException(
            "送信できませんでした", new SocketException((int)SocketError.ConnectionRefused));

        var result = await h.Checker(log: log).CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdateFailureKind.CannotConnect, result.Failure!.Kind);

        // 外側だけでなく、内側の例外まで残っている
        Assert.Contains(log, l =>
            l.Contains("HttpRequestException: 送信できませんでした", StringComparison.Ordinal) &&
            l.Contains("SocketException", StringComparison.Ordinal));

        // 画面の文言に型名は出ない
        Assert.DoesNotContain("Exception", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 時間切れは時間内に応答が無かったと伝える()
    {
        var h = new Routes();
        h.ThrowFor = _ => new TaskCanceledException("timeout", new TimeoutException());

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateFailureKind.Timeout, result.Failure!.Kind);
    }

    [Fact]
    public async Task 呼び出し側が止めたときは失敗にせず取り消しを伝える()
    {
        var h = new Routes { [AtomUrl] = _ => Atom("v1.0.5") };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Checker().CheckAsync(cts.Token));
    }

    // ---- Atom が使えないとき ------------------------------------------------

    [Fact]
    public async Task Atomが壊れていてもAPIで確かめる()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Status(HttpStatusCode.OK, "<feed>閉じていない", "application/atom+xml"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal([AtomUrl, ApiUrl], h.Requested);
        Assert.False(result.Info!.DetailsUnavailable);
    }

    [Fact]
    public async Task AtomがDTD付きでもAPIで確かめる()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE feed [ <!ENTITY x SYSTEM "file:///etc/passwd"> ]>
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
              <link href="https://github.com/Yu5rin/Kado/releases/tag/v9.9.9"/></entry></feed>
            """;

        var h = new Routes
        {
            [AtomUrl] = _ => Status(HttpStatusCode.OK, xxe, "application/atom+xml"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        var result = await h.Checker().CheckAsync();

        // v9.9.9 は読まれず、API の v1.0.5 になる
        Assert.Equal(new Version(1, 0, 5), result.Info!.Version);
    }

    [Fact]
    public async Task Atomが大きすぎてもAPIで確かめる()
    {
        var big = AtomFeedTests.Feed("v9.9.9")
            .Replace("<title>Release notes from Kado</title>", $"<title>{new string('x', AtomFeed.MaxBytes)}</title>");

        var h = new Routes
        {
            [AtomUrl] = _ => Status(HttpStatusCode.OK, big, "application/atom+xml"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(new Version(1, 0, 5), result.Info!.Version);
    }

    [Fact]
    public async Task AtomがWebページを返してもAPIで確かめる()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Html("<html>このサイトはブロックされています</html>"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        var result = await h.Checker().CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task GitHub以外の問い合わせ先ではAPIだけを見る()
    {
        const string other = "https://example.com/releases/latest";
        var h = new Routes { [other] = _ => Json(ReleaseJson) };

        var result = await h.Checker(apiUrl: other).CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal([other], h.Requested);
    }

    [Fact]
    public async Task GitHub以外でAPIが失敗したら組み立てずに失敗にする()
    {
        const string other = "https://example.com/releases/latest";
        var h = new Routes { [other] = _ => Status(HttpStatusCode.Forbidden) };

        var result = await h.Checker(apiUrl: other).CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(string.Empty, result.Failure!.ReleasePageUrl);
    }

    // ---- 記録 ---------------------------------------------------------------

    [Fact]
    public async Task プロキシの経路は一度だけ記録する()
    {
        var log = new List<string>();
        var h = new Routes { [AtomUrl] = _ => Atom("v1.0.4") };
        var checker = h.Checker(log: log, proxy: new UpdateDiagnosticsTests.FakeProxy(new Uri("http://proxy.corp.example:8080")));

        await checker.CheckAsync();
        await checker.CheckAsync();
        await checker.ProbeConnectionAsync();

        Assert.Single(log, l => l.StartsWith("更新の通信: ", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("proxy.corp.example:8080", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 問い合わせ先と応答をひと通り記録する()
    {
        var log = new List<string>();
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
        };

        await h.Checker(log: log).CheckAsync();

        Assert.Contains(log, l => l.Contains("更新の確認: Atom に問い合わせる " + AtomUrl, StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Atom 応答 200 OK", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("API に問い合わせる " + ApiUrl, StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("API 応答 200 OK", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("新しい版がある", StringComparison.Ordinal));
    }

    // ---- 通信の試験 ---------------------------------------------------------

    private const string AssetUrl = "https://github.com/Yu5rin/Kado/releases/download/v1.0.5/Kado-1.0.5-win-x64.exe";

    [Fact]
    public async Task 通信の試験は3か所へ接続して先頭だけ受け取る()
    {
        var log = new List<string>();
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
            [AssetUrl] = _ => Exe(size: 5 * 1024 * 1024),
        };

        var report = await h.Checker(log: log).ProbeConnectionAsync();

        Assert.True(report.AllOk);
        Assert.Equal(3, report.Steps.Count);
        Assert.Equal([AtomUrl, ApiUrl, AssetUrl], h.Requested);

        // 5MB のファイルを、先頭の 256KB だけ受け取って切る
        Assert.Contains("256KB", report.Steps[2].Detail, StringComparison.Ordinal);
        Assert.Contains("すべて届きました", report.ToDisplayText(), StringComparison.Ordinal);

        // 記録にも残る。ファイルは保存していない旨も書く
        Assert.Contains(log, l => l.Contains("配布ファイルの置き場: 262144バイトを受け取れた（先頭のみ。ファイルは保存していない）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 通信の試験は最新版でも組み立てたURLで配布ファイルへ接続する()
    {
        // 「更新の確認」は最新版なら配布ファイルに触れない。試験は更新の要否と無関係に試す
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.4"),
            [ApiUrl] = _ => Json(ReleaseJson),
            ["https://github.com/Yu5rin/Kado/releases/download/v1.0.4/Kado-1.0.4-win-x64.exe"] = _ => Exe(1024),
        };

        var report = await h.Checker(current: new Version(1, 0, 4)).ProbeConnectionAsync();

        Assert.True(report.AllOk);
        Assert.Equal(
            "https://github.com/Yu5rin/Kado/releases/download/v1.0.4/Kado-1.0.4-win-x64.exe", h.Requested[^1]);
    }

    [Fact]
    public async Task 通信の試験はAPIの状態コードを結果に出す()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Status(HttpStatusCode.Forbidden),
            [AssetUrl] = _ => Exe(1024),
        };

        var report = await h.Checker().ProbeConnectionAsync();

        Assert.False(report.AllOk);
        Assert.False(report.Steps[1].Ok);
        Assert.Contains("HTTP 403 Forbidden", report.Steps[1].Detail, StringComparison.Ordinal);
        Assert.Contains("問い合わせ回数の上限", report.Steps[1].Detail, StringComparison.Ordinal);

        // Atom と配布ファイルが届くなら、更新はできる
        Assert.Contains("リリース情報だけが届いていません", report.ToDisplayText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通信の試験は配布ファイルの代わりにWebページが返ったら失敗にする()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
            [AssetUrl] = _ => Html("<html>ブロックされました</html>"),
        };

        var report = await h.Checker().ProbeConnectionAsync();

        Assert.False(report.Steps[2].Ok);
        Assert.Contains("Web ページ", report.Steps[2].Detail, StringComparison.Ordinal);
        Assert.Contains("届かない場所があります", report.ToDisplayText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通信の試験は実行ファイルの形でない中身を失敗にする()
    {
        // Content-Type は octet-stream でも、中身が別物（中継が差し替えた）
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
            [AssetUrl] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("<html>error</html>"u8.ToArray())
                {
                    Headers = { ContentType = new("application/octet-stream") },
                },
            },
        };

        var report = await h.Checker().ProbeConnectionAsync();

        Assert.False(report.Steps[2].Ok);
        Assert.Contains("実行ファイルではない", report.Steps[2].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 通信の試験は転送先も許可された場所か確かめて記録には署名を残さない()
    {
        var log = new List<string>();
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
            [AssetUrl] = _ => Redirect("https://objects.githubusercontent.com/release-asset/1/abc?sp=r&sig=SECRET"),
            ["https://objects.githubusercontent.com/release-asset/1/abc?sp=r&sig=SECRET"] = _ => Exe(1024),
        };

        var report = await h.Checker(log: log).ProbeConnectionAsync();

        Assert.True(report.AllOk);
        Assert.Contains(log, l => l.Contains("配布ファイルの置き場: 転送 302 → https://objects.githubusercontent.com/release-asset/1/abc", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("配布ファイルの置き場: 転送先=https://objects.githubusercontent.com/release-asset/1/abc", StringComparison.Ordinal));

        // 署名（一時的な鍵）は、どの行にも出ない
        Assert.DoesNotContain(log, l => l.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 通信の試験は許可されない転送先へは行かない()
    {
        var h = new Routes
        {
            [AtomUrl] = _ => Atom("v1.0.5"),
            [ApiUrl] = _ => Json(ReleaseJson),
            [AssetUrl] = _ => Redirect("https://evil.example.com/Kado.exe"),
            ["https://evil.example.com/Kado.exe"] = _ => Exe(1024),
        };

        var report = await h.Checker().ProbeConnectionAsync();

        Assert.False(report.Steps[2].Ok);
        Assert.DoesNotContain("https://evil.example.com/Kado.exe", h.Requested);
    }

    [Fact]
    public async Task 通信の試験は接続できない理由を短く出す()
    {
        var log = new List<string>();
        var h = new Routes();
        h.ThrowFor = _ => new HttpRequestException(
            "送信できませんでした", new SocketException((int)SocketError.ConnectionRefused));

        var report = await h.Checker(log: log).ProbeConnectionAsync();

        Assert.All(report.Steps, s => Assert.False(s.Ok));
        Assert.All(report.Steps, s => Assert.Contains("接続できない", s.Detail, StringComparison.Ordinal));

        // 型名は画面に出さず、記録にだけ出す
        Assert.DoesNotContain("Exception", report.ToDisplayText(), StringComparison.Ordinal);
        Assert.Contains(log, l => l.Contains("SocketException", StringComparison.Ordinal));
    }

    // ---- 部品 ---------------------------------------------------------------

    private static HttpResponseMessage Atom(params string[] tags) =>
        Status(HttpStatusCode.OK, AtomFeedTests.Feed(tags), "application/atom+xml");

    private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body, "application/json");

    private static HttpResponseMessage Html(string body) => Status(HttpStatusCode.OK, body, "text/html");

    private static HttpResponseMessage Status(HttpStatusCode status, string body = "", string contentType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    /// <summary>実行ファイルの形（先頭が MZ）をした、指定の大きさの応答。</summary>
    private static HttpResponseMessage Exe(int size)
    {
        var bytes = new byte[size];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/octet-stream");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>URL ごとに決まった応答を返す HTTP。どこへ問い合わせたかを控える。</summary>
    private sealed class Routes : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];

        /// <summary>問い合わせた URL（クエリを含む）を順に。</summary>
        public List<string> Requested { get; } = [];

        private readonly Dictionary<string, string> _accepts = [];

        /// <summary>接続の失敗など、応答を返さずに投げる例外。設定すると全 URL に効く。</summary>
        public Func<HttpRequestMessage, Exception>? ThrowFor { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage> this[string url]
        {
            set => _routes[url] = value;
        }

        public string? AcceptOf(string url) => _accepts.GetValueOrDefault(url);

        public UpdateChecker Checker(
            string apiUrl = ApiUrl, Version? current = null, List<string>? log = null, System.Net.IWebProxy? proxy = null) =>
            new(apiUrl, current ?? Current,
                // ハンドラは使い回すので、クライアントを捨てるときに一緒に捨てない
                _ => new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                log is null ? null : log.Add, proxy);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = request.RequestUri!.ToString();

            Requested.Add(url);
            _accepts[url] = string.Join(",", request.Headers.Accept);

            if (ThrowFor is { } thrower) throw thrower(request);

            if (!_routes.TryGetValue(url, out var respond))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }

            var response = respond(request);
            response.RequestMessage = request;

            return Task.FromResult(response);
        }
    }
}
