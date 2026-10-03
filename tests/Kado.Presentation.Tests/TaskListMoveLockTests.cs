using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// サブタスクと、サブタスクを持つタスクは、Google でリストをまたいで移せない。
/// 変えさせても同期のたびに断られるだけなので、編集画面のリスト欄を止める。
/// </summary>
public class TaskListMoveLockTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly SourceChoice[] Lists = [new("list-a", "仕事"), new("list-b", "私用")];

    private static TaskItem Task(string id, string? googleId = null, string? parent = null) => new()
    {
        Id = id, Title = id, Due = D(2026, 9, 24), TaskListId = "list-a",
        GoogleTaskId = googleId, GoogleTaskListId = googleId is null ? null : "list-a", ParentId = parent,
        UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void サブタスクはリストを変えさせない()
    {
        var vm = new TaskEditorViewModel(Task("c1", "gc1", parent: "gp1"), Lists, D(2026, 9, 24));

        Assert.False(vm.CanChangeTaskList);
        Assert.Contains("サブタスク", vm.TaskListLockReason, StringComparison.Ordinal);

        vm.TaskListId = "list-b";
        Assert.Equal("list-a", vm.TaskListId);
    }

    [Fact]
    public void サブタスクを持つ親もリストを変えさせない()
    {
        var vm = new TaskEditorViewModel(Task("p1", "gp1"), Lists, D(2026, 9, 24), hasChildren: true);

        Assert.False(vm.CanChangeTaskList);
        Assert.NotNull(vm.TaskListLockReason);

        vm.TaskListId = "list-b";
        Assert.Equal("list-a", vm.TaskListId);
    }

    [Fact]
    public void ふつうのタスクと新しいタスクはリストを選べる()
    {
        var plain = new TaskEditorViewModel(Task("t1", "g1"), Lists, D(2026, 9, 24));
        Assert.True(plain.CanChangeTaskList);
        plain.TaskListId = "list-b";
        Assert.Equal("list-b", plain.TaskListId);

        Assert.True(new TaskEditorViewModel(D(2026, 9, 24), Lists, D(2026, 9, 24)).CanChangeTaskList);
    }

    [Fact]
    public void 親を開くと手元の親子関係から子を持つと分かり_リストを変えさせない()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var vm = new MainViewModel(test.Workspace, today: D(2026, 9, 24), editors: editors);

        // 子は Google の ID で親を指す（ParentId は Google の ID）
        test.Workspace.Tasks.Upsert(Task("local-parent", "gp1"));
        test.Workspace.Tasks.Upsert(Task("local-child", "gc1", parent: "gp1"));

        editors.OnTask = _ => false;
        vm.EditTaskChipCommand.Execute(new Kado.Data.Repositories.ScheduledTask(
            test.Workspace.Tasks.Find("local-parent")!, D(2026, 9, 24)));

        Assert.NotNull(editors.LastTaskEditor);
        Assert.False(editors.LastTaskEditor!.CanChangeTaskList);
    }
}
