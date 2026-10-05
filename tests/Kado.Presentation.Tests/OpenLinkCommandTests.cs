using System.ComponentModel;
using Kado.Data.Models;
using Kado.Presentation.Links;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 右クリックメニューの「リンクを開く」「添付を開く」（予定・タスク）。
/// 実際の起動は <see cref="FakeLinkOpener"/> に差し替え、何を起動しようとしたか・
/// 開けなかったときにステータス行へ何が出るかを見る。
/// </summary>
public class OpenLinkCommandTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static (MainViewModel Vm, FakeLinkOpener Opener) Create(TestWorkspace test)
    {
        var opener = new FakeLinkOpener();
        return (new MainViewModel(test.Workspace, today: D(2026, 9, 24), linkOpener: opener), opener);
    }

    private static EventAttachment Drive(string url, string title = "資料.pdf") => new("id", url, title, "application/pdf");

    // ------------------------------------------------------------------
    // 予定のリンク
    // ------------------------------------------------------------------

    [Fact]
    public void 予定のリンクを既定のブラウザで開く()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenEventLinkCommand.Execute("https://example.com/spec");

        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://example.com/spec")], opener.Launched);
        Assert.Null(vm.StatusMessage);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    public void 予定のリンクがhttp以外なら開かず理由を出す(string url)
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenEventLinkCommand.Execute(url);

        Assert.Empty(opener.Launched);
        Assert.Contains("http", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ブラウザを起動できなければステータス行に理由を出す()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);
        opener.Throws = new Win32Exception("既定のブラウザがありません");

        vm.OpenEventLinkCommand.Execute("https://example.com/spec");

        Assert.Contains("開けませんでした", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("既定のブラウザがありません", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 空のリンクでは何も起きない()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenEventLinkCommand.Execute(null);
        vm.OpenEventLinkCommand.Execute("   ");
        vm.OpenTaskLinkCommand.Execute(null);

        Assert.Empty(opener.Launched);
        Assert.Null(vm.StatusMessage);
    }

    // ------------------------------------------------------------------
    // 予定の添付
    // ------------------------------------------------------------------

    [Fact]
    public void 予定の添付はhttpsなら開く()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenEventAttachmentCommand.Execute(Drive("https://drive.google.com/file/d/1/view"));

        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://drive.google.com/file/d/1/view")], opener.Launched);
    }

    [Fact]
    public void 予定の添付がhttpsでなければ開かず理由を出す()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenEventAttachmentCommand.Execute(Drive("http://drive.google.com/file/d/1/view"));

        Assert.Empty(opener.Launched);
        Assert.Equal("この添付は開けません（https の URL ではありません）", vm.StatusMessage);
    }

    // ------------------------------------------------------------------
    // タスク
    // ------------------------------------------------------------------

    [Fact]
    public void タスクのリンクも予定と同じ決まりで開く()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenTaskLinkCommand.Execute("https://example.com/task");
        vm.OpenTaskLinkCommand.Execute("file:///C:/a.exe");

        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://example.com/task")], opener.Launched);
        Assert.Contains("http", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task タスクのファイルが見つからなければ開かずに場所を添えて知らせる()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        // 実際のファイルシステムに存在しない場所（Linux でも Windows でも無い）
        await vm.OpenTaskAttachmentAsync(new TaskAttachment(@"Z:\kado-test-存在しない\図面.pdf"));

        Assert.Empty(opener.Launched);
        Assert.Equal(@"見つかりません：Z:\kado-test-存在しない\図面.pdf", vm.StatusMessage);
    }

    [Fact]
    public async Task タスクのファイルの場所でないものは開かない()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        await vm.OpenTaskAttachmentAsync(new TaskAttachment("https://example.com/a.pdf"));
        await vm.OpenTaskAttachmentAsync(new TaskAttachment(@"..\a.pdf"));

        Assert.Empty(opener.Launched);
        Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public async Task 画面から呼ぶコマンドも同じ結果を出す()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        vm.OpenTaskAttachmentCommand.Execute(new TaskAttachment(@"Z:\kado-test-存在しない\図面.pdf"));

        // コマンドは待たずに戻る。結果（ステータス行）が出るのを少し待つ
        for (var i = 0; i < 200 && vm.StatusMessage is null; i++) await Task.Delay(10);

        Assert.Empty(opener.Launched);
        Assert.Equal(@"見つかりません：Z:\kado-test-存在しない\図面.pdf", vm.StatusMessage);
    }

    // ------------------------------------------------------------------
    // 「添付を開く」の子メニュー
    // ------------------------------------------------------------------

    [Fact]
    public void 子メニューの項目は予定とタスクで起こすコマンドが違う()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        var eventItems = vm.AttachmentMenuItems(OpenTargets.For(new CalendarEvent
        {
            Id = "e1", Title = "定例",
            PendingAttachments = Kado.Google.Mapping.EventMapper.ToPendingAttachmentsJson(
                [Drive("https://drive.google.com/file/d/1/view", "資料.pdf")]),
        }));
        var taskItems = vm.AttachmentMenuItems(OpenTargets.For(new TaskItem
        {
            Id = "t1", Title = "集計",
            Attachments = TaskAttachments.ToJson([new TaskAttachment(@"C:\資料\図面.pdf")]),
        }));

        var forEvent = Assert.Single(eventItems);
        Assert.Equal("資料.pdf", forEvent.Label);
        Assert.Same(vm.OpenEventAttachmentCommand, forEvent.Command);
        Assert.IsType<EventAttachment>(forEvent.Parameter);

        var forTask = Assert.Single(taskItems);
        Assert.Equal("図面.pdf", forTask.Label);
        Assert.Same(vm.OpenTaskAttachmentCommand, forTask.Command);
        Assert.IsType<TaskAttachment>(forTask.Parameter);
    }

    [Fact]
    public void 開ける先が無ければ子メニューは空()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);

        Assert.Empty(vm.AttachmentMenuItems(null));
        Assert.Empty(vm.AttachmentMenuItems(OpenTargets.None));
    }

    [Fact]
    public void 子メニューの項目を押すと開く()
    {
        using var test = TestWorkspace.Create();
        var (vm, opener) = Create(test);

        var item = Assert.Single(vm.AttachmentMenuItems(OpenTargets.For(new CalendarEvent
        {
            Id = "e1", Title = "定例",
            PendingAttachments = Kado.Google.Mapping.EventMapper.ToPendingAttachmentsJson(
                [Drive("https://drive.google.com/file/d/1/view")]),
        })));

        item.Command.Execute(item.Parameter);

        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://drive.google.com/file/d/1/view")], opener.Launched);
    }

    // ------------------------------------------------------------------
    // 予定の編集画面も同じ決まり（二重に書いていない）
    // ------------------------------------------------------------------

    [Fact]
    public void 予定の編集画面の添付も右クリックと同じ口で開く()
    {
        var opener = new FakeLinkOpener();
        var editor = new Kado.Presentation.Editing.EventEditorViewModel(
            D(2026, 9, 24), [new Kado.Presentation.Editing.SourceChoice("primary", "仕事")],
            links: new LinkLauncher(opener));

        editor.OpenAttachment(Drive("https://drive.google.com/file/d/1/view"));
        Assert.Equal([new LaunchRequest(LaunchKind.Shell, "https://drive.google.com/file/d/1/view")], opener.Launched);
        Assert.Null(editor.AttachmentError);

        editor.OpenAttachment(Drive("http://drive.google.com/file/d/1/view"));
        Assert.Single(opener.Launched);
        Assert.Equal("この添付は開けません（https の URL ではありません）", editor.AttachmentError);
    }

    [Fact]
    public void 予定の編集画面でブラウザを起動できなければ理由を出す()
    {
        var opener = new FakeLinkOpener { Throws = new Win32Exception("既定のブラウザがありません") };
        var editor = new Kado.Presentation.Editing.EventEditorViewModel(
            D(2026, 9, 24), [new Kado.Presentation.Editing.SourceChoice("primary", "仕事")],
            links: new LinkLauncher(opener));

        editor.OpenAttachment(Drive("https://drive.google.com/file/d/1/view"));

        Assert.Contains("既定のブラウザがありません", editor.AttachmentError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 読むだけの機能は、データに触らない
    // ------------------------------------------------------------------

    [Fact]
    public async Task 開いても予定とタスクは書き換わらない()
    {
        using var test = TestWorkspace.Create();
        var (vm, _) = Create(test);
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "定例", Date = D(2026, 9, 24), Url = "https://example.com/e" });
        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "集計", Due = D(2026, 9, 24), Url = "https://example.com/t" });
        var eventBefore = test.Workspace.Events.Find("e1");
        var taskBefore = test.Workspace.Tasks.Find("t1");
        var undoBefore = test.Workspace.Undo.UndoDescription;

        vm.OpenEventLinkCommand.Execute("https://example.com/e");
        vm.OpenTaskLinkCommand.Execute("https://example.com/t");
        await vm.OpenTaskAttachmentAsync(new TaskAttachment(@"Z:\kado-test-存在しない\a.pdf"));

        Assert.Equal(eventBefore, test.Workspace.Events.Find("e1"));
        Assert.Equal(taskBefore, test.Workspace.Tasks.Find("t1"));
        Assert.Equal(undoBefore, test.Workspace.Undo.UndoDescription);
    }
}
