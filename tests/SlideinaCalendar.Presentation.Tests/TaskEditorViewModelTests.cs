using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

public class TaskEditorViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void タイトルが空なら保存できない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), ["マイタスク"]);

        Assert.False(vm.CanSave);

        vm.Title = "台数計画の確定";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void 期限を外せる()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), ["マイタスク"]) { Title = "いつかやる" };

        Assert.Equal(D(2026, 9, 24), vm.ToModel().Due);

        vm.HasDue = false;

        // 「期限だけある」「いつやるか未定」を持てる必要がある（要件書 3.1）
        Assert.Null(vm.ToModel().Due);
    }

    [Fact]
    public void 期限を外しても日付は覚えている()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), []) { Title = "提出" };

        vm.HasDue = false;
        vm.HasDue = true;

        Assert.Equal(D(2026, 9, 24), vm.ToModel().Due);
    }

    [Fact]
    public void 期限の無いタスクを開くと今日が入る()
    {
        var source = new TaskItem { Id = "t1", Title = "未定", Due = null };
        var vm = new TaskEditorViewModel(source, [], fallbackDue: D(2026, 9, 24));

        Assert.False(vm.HasDue);
        Assert.Equal(D(2026, 9, 24), vm.Due);   // 付けると決めたときの初期値
    }

    [Fact]
    public void 既存を直しても識別子とGoogleの情報は残る()
    {
        var source = new TaskItem
        {
            Id = "t1", Title = "集計", Due = D(2026, 9, 24),
            GoogleTaskId = "g1", GoogleTaskListId = "gl1", Source = "google",
        };

        var vm = new TaskEditorViewModel(source, ["マイタスク"], D(2026, 9, 24)) { Title = "集計（変更）" };
        var model = vm.ToModel();

        Assert.Equal("t1", model.Id);
        Assert.Equal("g1", model.GoogleTaskId);
        Assert.Equal("gl1", model.GoogleTaskListId);
        Assert.Equal("google", model.Source);
    }
}
