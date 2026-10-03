using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 落ちる・使えなくなる筋を塞いだ配線を、アプリを起動せずに検査する。
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは参照できない
/// （<see cref="BackupWiringTests"/> と同じ理由）。実機でしか確かめられない配線は、
/// 直した書き方が実在するか、戻されていないかをソースで見張る。
/// 判断そのものは <c>Kado.Shell.Tests</c>・<c>Kado.Presentation.Tests</c> で試験している。
/// </para>
/// </summary>
public class ResilienceWiringTests
{
    private static string AppDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string App { get; } = Read("App.xaml.cs");

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} が見つかりません。");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} が見つかりません。");

        return source[from..to];
    }

    // ------------------------------------------------------------------
    // 項目1: 添付
    // ------------------------------------------------------------------

    [Fact]
    public void 添付を足すasyncVoidは最後の砦のtryを持つ()
    {
        var handler = Slice(
            Read("Views/EventEditorWindow.xaml.cs"), "private async void OnAddAttachmentClick", "OnRemoveAttachmentClick");

        Assert.Contains("try", handler, StringComparison.Ordinal);
        Assert.Contains("catch (Exception", handler, StringComparison.Ordinal);
        Assert.Contains("ReportAttachmentFailure", handler, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目2: 更新
    // ------------------------------------------------------------------

    [Fact]
    public void 入れ替えは別スレッドで行い起動の失敗も巻き戻す()
    {
        var service = Read("Update/UpdateService.cs");

        Assert.Contains("Task.Run(() => Apply(downloadedExe))", service, StringComparison.Ordinal);
        Assert.Contains("ExecutableSwap.Run", service, StringComparison.Ordinal);
    }

    [Fact]
    public void 更新の窓は入れ替えの失敗を専用の文言で出す()
    {
        var window = Read("Views/UpdateWindow.xaml.cs");

        Assert.Contains("await _updater.ApplyAsync(", window, StringComparison.Ordinal);
        Assert.Contains("swap.Message", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_updater.Apply(", window, StringComparison.Ordinal);
    }

    [Fact]
    public void リリースのページを開くProcessStartも受ける()
    {
        var open = Slice(Read("Views/UpdateWindow.xaml.cs"), "private void OpenPage_Click", "protected override void OnClosing");

        Assert.Contains("catch", open, StringComparison.Ordinal);
        Assert.Contains("Win32Exception", open, StringComparison.Ordinal);
    }

    [Fact]
    public void 待機カーソルは窓やメッセージを出す前に戻す()
    {
        var check = Slice(App, "private async Task CheckForUpdateAsync", "private void ShowUpdateFailure");

        var reset = check.IndexOf("Mouse.OverrideCursor = null", StringComparison.Ordinal);
        var dialog = check.IndexOf("new UpdateWindow", StringComparison.Ordinal);
        var message = check.IndexOf("MessageBox.Show", StringComparison.Ordinal);

        Assert.True(reset >= 0);
        Assert.True(reset < dialog, "更新の窓（ShowDialog）より前にカーソルを戻すこと");
        Assert.True(reset < message, "メッセージより前にカーソルを戻すこと");
    }

    // ------------------------------------------------------------------
    // 項目5: 復元
    // ------------------------------------------------------------------

    [Fact]
    public void 復元は何かを閉じる前に復元元を確かめる()
    {
        var restore = Slice(App, "private void RestoreAndRestart", "private static void OpenInBrowser");

        var check = restore.IndexOf("EnsureReadableDatabase", StringComparison.Ordinal);
        var teardown = restore.IndexOf("_background?.Dispose()", StringComparison.Ordinal);

        Assert.True(check >= 0);
        Assert.True(check < teardown);
    }

    [Fact]
    public void 復元に失敗したら元のデータのまま立ち上げ直す()
    {
        var restore = Slice(App, "private void RestoreAndRestart", "private static void OpenInBrowser");

        Assert.Matches(new Regex(@"catch \(Exception ex\) when \(ex is IOException[^)]*SqliteException\)"), restore);
        Assert.Contains("RestartProcess();", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void 立ち上げ直しは前のプロセスの終了を待つ引数を付け失敗も拾う()
    {
        var launch = Slice(App, "private static bool TryLaunchSelf", "// データベースを開けなかったとき");

        Assert.Contains("StartupArguments.AfterRestart", launch, StringComparison.Ordinal);
        Assert.Contains("Win32Exception", launch, StringComparison.Ordinal);
        Assert.Contains("StartupArguments.WaitsForPreviousProcess", App, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目6: トレイ
    // ------------------------------------------------------------------

    [Fact]
    public void トレイの受け皿はメッセージ専用の窓ではなくTaskbarCreatedを受ける()
    {
        var tray = Read("Shell/TrayIcon.cs");

        // HWND_MESSAGE（-3）を親にすると、ブロードキャストの TaskbarCreated が届かない
        Assert.DoesNotContain("new IntPtr(-3)", tray, StringComparison.Ordinal);
        Assert.Contains("RegisterWindowMessage(\"TaskbarCreated\")", tray, StringComparison.Ordinal);

        // タスクバーにも Alt+Tab にも出さない
        Assert.Contains("WS_EX_TOOLWINDOW", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void トレイは置けなかったらやり直し置き直しも行う()
    {
        var tray = Read("Shell/TrayIcon.cs");

        Assert.Contains("TrayPolicy.RetryDelay", tray, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"msg == _taskbarCreated.*?TryAddOrRetry\(\)", RegexOptions.Singleline), tray);
    }

    [Fact]
    public void 閉じるボタンはトレイに出ていないあいだ窓を隠さない()
    {
        var closing = Slice(App, "private void OnMainWindowClosing", "private void ReleaseShellFromAnyThread");

        Assert.Contains("TrayPolicy.HidesOnClose", closing, StringComparison.Ordinal);
        Assert.Contains("_tray?.IsShown", closing, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目7: データベースを開けなかったとき
    // ------------------------------------------------------------------

    [Fact]
    public void データベースを開けなかったときは種類を見分けてからにする()
    {
        var open = Slice(App, "private SqliteConnection? OpenDatabaseOrAskUser", "private void HandleBrokenDatabase");

        Assert.Contains("DatabaseOpenFailure.Classify", open, StringComparison.Ordinal);
        Assert.Contains("DatabaseOpenFailure.OffersSetAside", open, StringComparison.Ordinal);
        Assert.Contains("RetryCancel", open, StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException", open, StringComparison.Ordinal);
    }

    [Fact]
    public void 起動のメッセージは親を持たず前面に出す()
    {
        // 起動の途中・ログオン直後のメッセージは、親が無いと他の窓の裏に隠れる
        var startupAndRecovery = Slice(App, "private SqliteConnection? OpenDatabaseOrAskUser", "protected override void OnExit");

        Assert.DoesNotContain("MessageBox.Show(", startupAndRecovery.Replace("FrontMessageBox.Show(", string.Empty),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目8: 画面のスレッドで漏れた書き込みの失敗
    // ------------------------------------------------------------------

    [Fact]
    public void 画面のスレッドの最後の網は直せる失敗なら続ける()
    {
        var handler = Slice(App, "DispatcherUnhandledException +=", "AppDomain.CurrentDomain.UnhandledException");

        Assert.Contains("UnhandledFailurePolicy.IsRecoverable", handler, StringComparison.Ordinal);

        var recoverable = handler.IndexOf("UnhandledFailurePolicy.IsRecoverable", StringComparison.Ordinal);
        var shutdown = handler.IndexOf("Shutdown(1)", StringComparison.Ordinal);
        Assert.True(recoverable < shutdown);
    }

    // ------------------------------------------------------------------
    // 項目9: 画面以外のスレッドで落ちたとき
    // ------------------------------------------------------------------

    [Fact]
    public void 画面以外のスレッドで落ちたときは画面のスレッドへ渡して外す()
    {
        var handler = Slice(App, "AppDomain.CurrentDomain.UnhandledException +=", "SessionEnding +=");

        Assert.Contains("ReleaseShellFromAnyThread()", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseShell();", handler, StringComparison.Ordinal);

        var marshal = Slice(App, "private void ReleaseShellFromAnyThread", "private void ReleaseShell()");
        Assert.Contains("Dispatcher.Invoke(", marshal, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(3)", marshal, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目10: シャットダウン
    // ------------------------------------------------------------------

    [Fact]
    public void 終了の問い合わせではAppBarを外さない()
    {
        Assert.Matches(new Regex(@"SessionEnding \+= \(_, args\) => HandleSessionEnding\(args\);"), App);

        var handler = Slice(App, "private void HandleSessionEnding", "private void OnSessionResolved");
        Assert.DoesNotContain("ReleaseShell", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void 終わるときの後片付けと取り消されたときの立ち上げ直しは結果の側にある()
    {
        var resolved = Slice(App, "private void OnSessionResolved", "private void ReleaseShellFromAnyThread");

        Assert.Contains("ReleaseShellFromAnyThread()", resolved, StringComparison.Ordinal);
        Assert.Contains("TryLaunchSelf()", resolved, StringComparison.Ordinal);
        Assert.Contains("new Shell.SessionEndWatcher(OnSessionResolved)", App, StringComparison.Ordinal);
    }

    [Fact]
    public void 見張りは独立した窓でWM_ENDSESSIONを受ける()
    {
        var watcher = Read("Shell/SessionEndWatcher.cs");

        Assert.Contains("case WM_QUERYENDSESSION", watcher, StringComparison.Ordinal);
        Assert.Contains("case WM_ENDSESSION", watcher, StringComparison.Ordinal);
        Assert.Contains("IsBackground = false", watcher, StringComparison.Ordinal);
        Assert.DoesNotContain("new IntPtr(-3)", watcher, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目11: 二重起動
    // ------------------------------------------------------------------

    [Fact]
    public void パイプ名はセッションとユーザーを含み待ち受けは間をおいてやり直す()
    {
        var single = Read("SingleInstance.cs");

        Assert.Contains("ActivationPipe.NameFor(", single, StringComparison.Ordinal);
        Assert.DoesNotContain("PipeName = \"Kado.Activate\"", single, StringComparison.Ordinal);
        Assert.Contains("ActivationPipe.RetryDelay", single, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目12: 起動の順序
    // ------------------------------------------------------------------

    [Fact]
    public void トークンの案内は最初の画面とトレイと合図の受け付けのあとに回す()
    {
        var listen = App.IndexOf("ListenForActivation(", StringComparison.Ordinal);
        var window = App.IndexOf("window.Show();", StringComparison.Ordinal);
        var shell = App.IndexOf("SetUpShell(window)", StringComparison.Ordinal);
        var notice = App.IndexOf("tokenStore.DecryptionFailed", StringComparison.Ordinal);

        Assert.True(listen >= 0 && window > listen, "合図の受け付けは窓を出す前に始める");
        Assert.True(shell > window);
        Assert.True(notice > shell, "案内は SetUpShell のあと");

        var block = Slice(App, "tokenStore.DecryptionFailed", "// 前回の入れ替えで残ったものを片付ける");
        Assert.Contains("DispatcherPriority.ApplicationIdle", block, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show(", block.Replace("FrontMessageBox.Show(", string.Empty),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目13: DPAPI
    // ------------------------------------------------------------------

    [Fact]
    public void 復号に失敗した控えはすぐ消さず退避する()
    {
        var store = Read("Google/DpapiTokenStore.cs");

        Assert.Contains("_quarantine.Load()", store, StringComparison.Ordinal);

        // 以前は CryptographicException を受けて、その場で消していた
        Assert.DoesNotContain("catch (CryptographicException)", store, StringComparison.Ordinal);
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kado.App");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Kado.App が見つかりません。");
    }
}
