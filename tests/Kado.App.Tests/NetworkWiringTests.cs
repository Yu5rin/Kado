namespace Kado.App.Tests;

/// <summary>
/// 通信まわりの配線を、アプリを起動せずに検査する。
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは参照できない
/// （<see cref="ResilienceWiringTests"/> と同じ理由）。実機でしか確かめられない配線は、
/// 直した書き方が実在するか、戻されていないかをソースで見張る。判断そのものは
/// <c>Kado.Presentation.Tests</c>・<c>Kado.Google.Tests</c>・<c>Kado.Core.Tests</c> で試験している。
/// </para>
/// </summary>
public class NetworkWiringTests
{
    private static string SrcDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(SrcDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string App { get; } = Read("Kado.App/App.xaml.cs");

    // ------------------------------------------------------------------
    // 項目1: 通信の作り方は1か所
    // ------------------------------------------------------------------

    [Fact]
    public void HttpClientを作るのは共通の工場だけ()
    {
        // 認証付きプロキシ（407）への対処（DefaultProxyCredentials）は、ハンドラの設定。
        // 別の場所で new HttpClient() や HttpClientHandler を作ると、そこだけ 407 で通らなくなる
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SrcDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SrcDirectory, file).Replace('\\', '/');

            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (relative.EndsWith("Kado.Core/Net/KadoHttp.cs", StringComparison.Ordinal)) continue;

            foreach (var (line, index) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;

                if (code.Contains("new HttpClient(", StringComparison.Ordinal) ||
                    code.Contains("new HttpClientHandler", StringComparison.Ordinal) ||
                    code.Contains("new SocketsHttpHandler", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{index}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "KadoHttp 以外で HttpClient を作っています: " + string.Join(", ", offenders));
    }

    [Fact]
    public void 工場はプロキシへログオン中のユーザーの資格情報を渡す()
    {
        var factory = Read("Kado.Core/Net/KadoHttp.cs");

        Assert.Contains("DefaultProxyCredentials = CredentialCache.DefaultCredentials", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void 更新も工場から作る()
    {
        var service = Read("Kado.App/Update/UpdateService.cs");

        Assert.Contains("KadoHttp.CreateClient(", service, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目2: 記録
    // ------------------------------------------------------------------

    [Fact]
    public void Googleと添付と実働日の配信は同じ記録先に書く()
    {
        Assert.Contains("new NetworkLog(Shell.ShellDiagnosticsLog.Write)", App, StringComparison.Ordinal);
        Assert.Contains("log: googleLog", App, StringComparison.Ordinal);
        Assert.Contains("workspace.Settings, googleLog", App, StringComparison.Ordinal);
        Assert.Contains("new WorkdayFeedClient(log:", App, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目3: 通信の始まりは Task.Run の中
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ConnectAsync")]
    [InlineData("DisconnectAsync")]
    [InlineData("SyncAsync")]
    [InlineData("EnsureDriveAttachmentScopeAsync")]
    public void Googleの入口は通信を画面のスレッドから始めない(string method)
    {
        var connection = Read("Kado.Presentation/Sync/GoogleConnection.cs");

        var start = connection.IndexOf($" {method}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{method} が見つかりません。");

        // 入口の本体（次の public メンバーまで）に Task.Run がある
        var body = connection[start..];
        var next = body.IndexOf("    public ", 10, StringComparison.Ordinal);

        Assert.Contains("Task.Run(", next > 0 ? body[..next] : body, StringComparison.Ordinal);
    }

    [Fact]
    public void 添付の通信はTaskRunの中で始め設定の読み書きは外で行う()
    {
        var uploader = Read("Kado.Presentation/Sync/GoogleDriveAttachmentUploader.cs");

        // 改行を挟んで書いてあるので、空白を許す
        Assert.Matches(@"Task\s*\.Run\(", uploader);
        Assert.Contains("api.UploadFileAsync(", uploader, StringComparison.Ordinal);
        Assert.Contains("api.CreateFolderAsync(", uploader, StringComparison.Ordinal);

        // 通信から戻ったら呼んだスレッドへ（画面側の DB に触る）
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(
                uploader, @"^(?>[ \t]*)(?!//).*ConfigureAwait\(false\)", System.Text.RegularExpressions.RegexOptions.Multiline),
            "添付は、通信のあとで画面側の DB に触る。ConfigureAwait(false) で別のスレッドへ流さない");
    }

    [Fact]
    public void 実働日の取得はTaskRunの中で始める()
    {
        var main = Read("Kado.Presentation/ViewModels/MainViewModel.cs");

        Assert.Contains("Task.Run(() => _feed.FetchAsync(url))", main, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目8: スリープ復帰・手動同期の成功
    // ------------------------------------------------------------------

    [Fact]
    public void 復帰したら裏の同期を起こす()
    {
        var resume = Slice(App, "void Recheck(ClockChange change)", "/// <summary>トレイのメニュー");

        Assert.Contains("ClockChangeRules.ShouldSyncAfter(change)", resume, StringComparison.Ordinal);
        Assert.Contains("_background?.SyncSoon()", resume, StringComparison.Ordinal);
    }

    [Fact]
    public void 同期が通ったら失敗の数えを戻す()
    {
        Assert.Contains("main.Sync.Succeeded += (_, _) => _background?.ReportSuccess()", App, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目9: 常駐中の更新の確認
    // ------------------------------------------------------------------

    [Fact]
    public void 常駐中の確認を始める()
    {
        Assert.Contains("StartUpdatePolling();", App, StringComparison.Ordinal);
        Assert.Contains("_updateSchedule.MarkChecked(DateTime.Now)", App, StringComparison.Ordinal);
    }

    [Fact]
    public void 設定で切っていれば何もしない()
    {
        var gate = Slice(App, "private void CheckForUpdateInBackgroundIfDue", "private async Task CheckForUpdateQuietlyAsync");

        Assert.Contains("CheckForUpdateOnStartup: true", gate, StringComparison.Ordinal);
        Assert.Contains("_updateSchedule.IsDue(", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void 静かな確認は更新の窓を勝手に開かない()
    {
        var quiet = Slice(App, "private async Task CheckForUpdateQuietlyAsync", "private void AnnounceUpdate");

        // 窓を開くのは、通知を押されたとき（OnUpdateBalloonClicked）だけ
        Assert.DoesNotContain("ShowUpdateWindow(", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("new UpdateWindow", quiet, StringComparison.Ordinal);
        Assert.Contains("AnnounceUpdate(result.Info!)", quiet, StringComparison.Ordinal);
    }

    [Fact]
    public void 通知を押すと更新の窓を開く()
    {
        Assert.Contains("_tray.BalloonClicked += (_, _) => Dispatcher.Invoke(OnUpdateBalloonClicked)", App, StringComparison.Ordinal);

        var tray = Read("Kado.App/Shell/TrayIcon.cs");
        Assert.Contains("NIN_BALLOONUSERCLICK", tray, StringComparison.Ordinal);
        Assert.Contains("BalloonClicked?.Invoke", tray, StringComparison.Ordinal);

        var click = Slice(App, "private void OnUpdateBalloonClicked", "private void ShowUpdateFailure");
        Assert.Contains("ShowUpdateWindow(info)", click, StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} が見つかりません。");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} が見つかりません。");

        return source[from..to];
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Kado.App"))) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src が見つかりません。");
    }
}
