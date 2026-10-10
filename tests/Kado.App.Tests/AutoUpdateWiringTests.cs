namespace Kado.App.Tests;

/// <summary>
/// 自動更新の配線を、アプリを起動せずに検査する。
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは参照できない
/// （<see cref="NetworkWiringTests"/> と同じ理由）。判断そのものは
/// <c>Kado.Presentation.Tests</c>（<c>AutoUpdatePolicyTests</c>・<c>StagedUpdateTests</c>）で試験している。
/// ここでは、その判断を通さずに入れ替える道が増えていないか、戻されていないかをソースで見張る。
/// </para>
/// </summary>
public class AutoUpdateWiringTests
{
    private static string SrcDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(SrcDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string App { get; } = Read("Kado.App/App.xaml.cs");

    private static string Auto { get; } = Read("Kado.App/Update/AutoUpdater.cs");

    [Fact]
    public void 設定画面に自動で更新するの欄がある()
    {
        var xaml = Read("Kado.App/Views/SettingsWindow.xaml");

        Assert.Contains("自動で更新する", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding AutoUpdate}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 起動時の入れ替えはメインウィンドウを出す前に行う()
    {
        var apply = App.IndexOf("ApplyStagedUpdateAtStartup)", StringComparison.Ordinal);
        var created = App.IndexOf("new MainWindow", StringComparison.Ordinal);
        var shown = App.IndexOf("window.Show();", StringComparison.Ordinal);

        Assert.True(apply > 0 && created > 0 && shown > 0);
        Assert.True(apply < created, "起動時の入れ替えが、窓を作ったあとに書かれています。");
        Assert.True(apply < shown);
    }

    [Fact]
    public void 起動時の入れ替えは終了の側では何もしない()
    {
        // 入れ替えは「次の起動の最初」にだけ行う。終了の経路（OnExit・サインアウト・シャットダウン）から
        // 入れ替えると、SessionEndWatcher の流れを邪魔する
        var exit = Slice(App, "protected override void OnExit", "private void HandleSessionEnding");
        var ending = Slice(App, "private void HandleSessionEnding", "private void OnSessionResolved");
        var resolved = Slice(App, "private void OnSessionResolved", "private void SetUpShell");

        foreach (var code in new[] { exit, ending, resolved })
        {
            Assert.DoesNotContain("_autoUpdater", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplyStaged", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 入れ替えてよいかは純粋な判断を通す()
    {
        var patrol = Slice(App, "private async Task AutoApplyIfIdleAsync", "private AutoUpdateState BuildAutoUpdateState");

        Assert.Contains("AutoUpdatePolicy.Wait(", patrol, StringComparison.Ordinal);
        Assert.Contains("_autoUpdater.Verify(", patrol, StringComparison.Ordinal);

        var state = Slice(App, "private AutoUpdateState BuildAutoUpdateState", "private void LogAutoUpdateWait");

        // 見る項目が減っていないこと
        foreach (var part in new[]
                 {
                     "OtherWindowOpen", "ModalDialogOpen", "MenuOrPopupOpen", "DragInProgress", "SyncRunning", "IdleFor",
                     "Shell.UserIdle.Current()", "PopupActivityHooks.Tracker", "DragActivity.IsDragging", "Sync.IsBusy",
                 })
        {
            Assert.Contains(part, state, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 入れ替えは手で更新するときと同じ部品を使う()
    {
        // 落とす・照合する・入れ替える・戻す・再起動する部分を二重に書かない
        Assert.Contains("_updater.DownloadAsync(", Auto, StringComparison.Ordinal);
        Assert.Contains("_updater.ApplyAsync(", Auto, StringComparison.Ordinal);
        Assert.Contains("_updater.Apply(", Auto, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "File.Move", "File.Copy", "Process.Start", "SHA256.", "HttpClient" })
        {
            Assert.DoesNotContain(forbidden, Auto, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 自動で落とすのは照合できる版だけ()
    {
        var stage = Slice(Auto, "public async Task<bool> TryStageAsync", "public StagedVerdict Verify");

        Assert.Contains("Decide(info)", stage, StringComparison.Ordinal);
        Assert.Contains("AutoDownloadDecision.Yes", stage, StringComparison.Ordinal);
        Assert.Contains("UpdateService.StagedDirectory", stage, StringComparison.Ordinal);
    }

    [Fact]
    public void 起動時の確認と常駐中の確認は裏で落とす_押しての確認は窓のまま()
    {
        var check = Slice(App, "private async Task CheckForUpdateAsync", "private void ShowUpdateWindow");
        Assert.Contains("!showWhenLatest && await TryStageInBackgroundAsync(", check, StringComparison.Ordinal);

        var quiet = Slice(App, "private async Task CheckForUpdateQuietlyAsync", "private void AnnounceUpdate");
        Assert.Contains("await TryStageInBackgroundAsync(", quiet, StringComparison.Ordinal);
    }

    [Fact]
    public void 更新の窓は控えがあれば落とし直さない()
    {
        var window = Read("Kado.App/Views/UpdateWindow.xaml.cs");

        Assert.Contains("_auto.FindUsableAsync(", window, StringComparison.Ordinal);
        Assert.Contains("_auto.ApplyAsync(", window, StringComparison.Ordinal);
    }

    [Fact]
    public void 更新後の知らせは一度だけ出し_押すと変更点の窓が開く()
    {
        Assert.Contains("TakeAppliedNotice()", App, StringComparison.Ordinal);

        var click = Slice(App, "private void OnUpdateBalloonClicked", "private void ShowUpdateFailure");
        Assert.Contains("BalloonTarget.Updated", click, StringComparison.Ordinal);
        Assert.Contains("ShowUpdatedNotice()", click, StringComparison.Ordinal);

        var window = Read("Kado.App/Views/UpdatedNoticeWindow.xaml.cs");
        Assert.Contains("変更点はリリースのページをご覧ください", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ドラッグは数えながら始める()
    {
        // 自動更新が、ドラッグの途中で再起動しないための目印。直に DoDragDrop を呼ぶ場所を増やさない
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(SrcDirectory, "Kado.App"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SrcDirectory, file).Replace('\\', '/');

            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (relative.EndsWith("Views/DragActivity.cs", StringComparison.Ordinal)) continue;

            foreach (var (line, index) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;

                if (code.Contains("DragDrop.DoDragDrop(", StringComparison.Ordinal)) offenders.Add($"{relative}:{index}");
            }
        }

        Assert.True(offenders.Count == 0, "DragActivity を通さずにドラッグを始めています: " + string.Join(", ", offenders));
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
