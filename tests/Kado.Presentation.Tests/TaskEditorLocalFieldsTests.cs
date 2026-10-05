using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.Links;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクの編集画面の、URL と添付（ファイルの場所）。<b>Kado だけが持つ項目</b>で、Google には送らない
/// （送らないことは <c>TaskLocalOnlyFieldsTests</c> が同期の側から確かめている）。
/// </summary>
public class TaskEditorLocalFieldsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly SourceChoice[] TaskLists = [new("local:mytasks", "マイタスク")];

    private static TaskEditorViewModel NewTask(FakeFileDialogs? dialogs = null, FakeLinkOpener? opener = null,
        Func<string, bool>? fileExists = null) =>
        new(D(2026, 9, 24), TaskLists, D(2026, 9, 24), dialogs: dialogs,
            links: opener is null ? null : new LinkLauncher(opener, fileExists ?? (_ => false), _ => false))
        {
            Title = "集計",
        };

    private static TaskEditorViewModel Edit(TaskItem value, FakeFileDialogs? dialogs = null, FakeLinkOpener? opener = null,
        Func<string, bool>? fileExists = null) =>
        new(value, TaskLists, D(2026, 9, 24), dialogs: dialogs,
            links: opener is null ? null : new LinkLauncher(opener, fileExists ?? (_ => false), _ => false));

    // ------------------------------------------------------------------
    // URL
    // ------------------------------------------------------------------

    [Fact]
    public void URLを入れて保存すると持ち帰れる()
    {
        var vm = NewTask();

        vm.Url = "  https://example.com/spec  ";

        Assert.Equal("https://example.com/spec", vm.ToModel().Url);
    }

    [Fact]
    public void URLを空にするとnullにする()
    {
        var vm = Edit(new TaskItem { Id = "t1", Title = "集計", Url = "https://example.com/spec" });

        Assert.Equal("https://example.com/spec", vm.Url);

        vm.Url = "   ";

        Assert.Null(vm.ToModel().Url);
    }

    [Fact]
    public void 開いたタスクのURLと添付が欄に入る()
    {
        var vm = Edit(new TaskItem
        {
            Id = "t1", Title = "集計", Url = "https://example.com/spec",
            Attachments = TaskAttachments.ToJson([new TaskAttachment(@"C:\資料\図面.pdf")]),
        });

        Assert.Equal("https://example.com/spec", vm.Url);
        Assert.Equal(@"C:\資料\図面.pdf", Assert.Single(vm.Attachments).Path);
    }

    [Theory]
    [InlineData("https://example.com/spec", false)]
    [InlineData("http://example.com/spec", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("www.example.com", true)]
    [InlineData("file:///C:/a.exe", true)]
    public void リンクとして開けない形のURLには説明を出す(string? url, bool hinted)
    {
        var vm = NewTask();

        vm.Url = url;

        Assert.Equal(hinted, vm.UrlHint is not null);

        // 保存は止めない（メモ代わりに置きたい人もいる）
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void 説明の文言にはGoogleに送らないことを書く()
    {
        Assert.Equal("URL と添付は Kado だけに保存され、Google には送られません", NewTask().LocalOnlyNote);
    }

    // ------------------------------------------------------------------
    // 添付（ファイルの場所）
    // ------------------------------------------------------------------

    [Fact]
    public void ファイルを複数選んで足せる()
    {
        var dialogs = new FakeFileDialogs();
        dialogs.FilesToPick.AddRange([@"C:\資料\図面.pdf", @"C:\資料\仕様.docx"]);
        var vm = NewTask(dialogs);

        vm.AddFiles();

        Assert.Equal([@"C:\資料\図面.pdf", @"C:\資料\仕様.docx"], vm.Attachments.Select(a => a.Path));

        var saved = TaskAttachments.Read(vm.ToModel().Attachments);
        Assert.Equal(vm.Attachments, saved);
    }

    [Fact]
    public void フォルダも足せる()
    {
        var dialogs = new FakeFileDialogs();
        dialogs.FoldersToPick.AddRange([@"C:\資料", @"\\server\share\議事録"]);
        var vm = NewTask(dialogs);

        vm.AddFolders();

        Assert.Equal([@"C:\資料", @"\\server\share\議事録"], vm.Attachments.Select(a => a.Path));
    }

    [Fact]
    public void 同じ場所は足さない()
    {
        var dialogs = new FakeFileDialogs();
        dialogs.FilesToPick.AddRange([@"C:\資料\図面.pdf"]);
        var vm = NewTask(dialogs);

        vm.AddFiles();
        vm.AddFiles();

        // 大文字小文字や末尾の区切りの違いも同じ場所
        dialogs.FilesToPick.Clear();
        dialogs.FilesToPick.Add(@"c:\資料\図面.PDF");
        vm.AddFiles();
        dialogs.FoldersToPick.AddRange([@"C:\資料", @"C:\資料\"]);
        vm.AddFolders();

        Assert.Equal([@"C:\資料\図面.pdf", @"C:\資料"], vm.Attachments.Select(a => a.Path));
    }

    [Fact]
    public void 選ぶのを取り消したら何も変わらない()
    {
        var dialogs = new FakeFileDialogs();
        var vm = Edit(new TaskItem { Id = "t1", Title = "集計", Attachments = "壊れた値" }, dialogs);

        vm.AddFiles();
        vm.AddFolders();

        Assert.Empty(vm.Attachments);

        // 触っていないので、読めなかった元の値もそのまま持ち帰る
        Assert.Equal("壊れた値", vm.ToModel().Attachments);
    }

    [Fact]
    public void 外せる()
    {
        var vm = Edit(new TaskItem
        {
            Id = "t1", Title = "集計",
            Attachments = TaskAttachments.ToJson([new TaskAttachment(@"C:\a.txt"), new TaskAttachment(@"C:\b.txt")]),
        });

        vm.RemoveAttachment(new TaskAttachment(@"C:\a.txt"));

        Assert.Equal([@"C:\b.txt"], vm.Attachments.Select(a => a.Path));
        Assert.Equal([@"C:\b.txt"], TaskAttachments.Read(vm.ToModel().Attachments).Select(a => a.Path));
    }

    [Fact]
    public void 全部外すとnullで保存する()
    {
        var vm = Edit(new TaskItem
        {
            Id = "t1", Title = "集計", Attachments = TaskAttachments.ToJson([new TaskAttachment(@"C:\a.txt")]),
        });

        vm.RemoveAttachment(new TaskAttachment(@"C:\a.txt"));

        Assert.Null(vm.ToModel().Attachments);
    }

    [Fact]
    public void 触っていない添付は元の文字列のまま持ち帰る()
    {
        // 末尾の区切りなど、こちらの書き方と違う保存済みの値でも、保存し直しただけで書き換えない
        const string original = """[ { "path" : "C:\\a.txt" } ]""";
        var vm = Edit(new TaskItem { Id = "t1", Title = "集計（直した）", Attachments = original });

        Assert.Equal(original, vm.ToModel().Attachments);
    }

    [Fact]
    public void 保存してもGoogleとの結び付きは変わらない()
    {
        var vm = Edit(new TaskItem
        {
            Id = "t1", Title = "集計", GoogleTaskId = "g1", GoogleTaskListId = "@default", GoogleRaw = "{}",
            Url = "https://example.com/old",
        });

        vm.Url = "https://example.com/new";
        var model = vm.ToModel();

        Assert.Equal("g1", model.GoogleTaskId);
        Assert.Equal("@default", model.GoogleTaskListId);
        Assert.Equal("{}", model.GoogleRaw);
    }

    // ------------------------------------------------------------------
    // 場所を開く
    // ------------------------------------------------------------------

    [Fact]
    public async Task 場所を開く()
    {
        var opener = new FakeLinkOpener();
        var vm = NewTask(opener: opener, fileExists: path => path == @"C:\資料\図面.pdf");

        await vm.OpenAttachmentAsync(new TaskAttachment(@"C:\資料\図面.pdf"));

        Assert.Equal([new LaunchRequest(LaunchKind.Shell, @"C:\資料\図面.pdf")], opener.Launched);
        Assert.Null(vm.AttachmentMessage);
    }

    [Fact]
    public async Task 見つからなければ画面に理由を出す()
    {
        var opener = new FakeLinkOpener();
        var vm = NewTask(opener: opener);

        await vm.OpenAttachmentAsync(new TaskAttachment(@"C:\消えた\図面.pdf"));

        Assert.Empty(opener.Launched);
        Assert.Equal(@"見つかりません：C:\消えた\図面.pdf", vm.AttachmentMessage);
    }

    [Fact]
    public async Task 実行形式は実行せずその旨を出す()
    {
        var opener = new FakeLinkOpener();
        var vm = NewTask(opener: opener, fileExists: _ => true);

        await vm.OpenAttachmentAsync(new TaskAttachment(@"C:\tools\setup.exe"));

        Assert.Equal([new LaunchRequest(LaunchKind.Reveal, @"C:\tools\setup.exe")], opener.Launched);
        Assert.NotNull(vm.AttachmentMessage);
    }

    [Fact]
    public async Task 次に開いたとき前の理由は消える()
    {
        var opener = new FakeLinkOpener();
        var vm = NewTask(opener: opener, fileExists: path => path == @"C:\ある.pdf");

        await vm.OpenAttachmentAsync(new TaskAttachment(@"C:\無い.pdf"));
        Assert.NotNull(vm.AttachmentMessage);

        await vm.OpenAttachmentAsync(new TaskAttachment(@"C:\ある.pdf"));
        Assert.Null(vm.AttachmentMessage);
    }

    // ------------------------------------------------------------------
    // 本体の編集の流れに乗る（保存・元に戻す・やり直す）
    // ------------------------------------------------------------------

    [Fact]
    public void 編集画面で付けたURLと添付が保存され元に戻せる()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var files = new FakeFileDialogs();
        files.FilesToPick.Add(@"C:\資料\図面.pdf");
        files.FoldersToPick.Add(@"\\server\share\議事録");
        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors, files: files);

        test.Workspace.AddTask(new TaskItem { Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks" });

        editors.OnTask = editor =>
        {
            editor.Url = "https://example.com/spec";
            editor.AddFiles();
            editor.AddFolders();
            return true;
        };
        vm.EditTaskCommand.Execute(vm.SelectedDay.Tasks.Single());

        var saved = test.Workspace.Tasks.Find("t1")!;
        Assert.Equal("https://example.com/spec", saved.Url);
        Assert.Equal(
            [@"C:\資料\図面.pdf", @"\\server\share\議事録"],
            TaskAttachments.Read(saved.Attachments).Select(a => a.Path));

        // 元に戻すと付ける前に戻り、やり直すと付いた姿に戻る
        vm.UndoCommand.Execute(null);

        var undone = test.Workspace.Tasks.Find("t1")!;
        Assert.Null(undone.Url);
        Assert.Null(undone.Attachments);

        vm.RedoCommand.Execute(null);

        var redone = test.Workspace.Tasks.Find("t1")!;
        Assert.Equal("https://example.com/spec", redone.Url);
        Assert.Equal(saved.Attachments, redone.Attachments);
    }

    [Fact]
    public void 開いている間に同期が結び付きを進めてもURLと添付は保存される()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors);

        test.Workspace.AddTask(new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24), TaskListId = "local:mytasks",
            GoogleTaskId = "g1", GoogleTaskListId = "@default", GoogleUpdated = "1", GoogleRaw = """{"id":"g1"}""",
        });

        editors.OnTask = editor =>
        {
            editor.Url = "https://example.com/spec";

            // 編集画面を開いている間に、裏の同期が結び付き（更新時刻と控え）を進めた
            test.Workspace.Tasks.Upsert(test.Workspace.Tasks.Find("t1")! with { GoogleUpdated = "2", GoogleRaw = """{"id":"g1","v":2}""" });
            return true;
        };

        editors.ConfirmsOverwrite = true;
        vm.EditTaskCommand.Execute(vm.SelectedDay.Tasks.Single());

        var saved = test.Workspace.Tasks.Find("t1")!;
        Assert.Equal("https://example.com/spec", saved.Url);

        // 結び付きは最新の行から採られる（古い姿で巻き戻さない）
        Assert.Equal("2", saved.GoogleUpdated);
    }
}
