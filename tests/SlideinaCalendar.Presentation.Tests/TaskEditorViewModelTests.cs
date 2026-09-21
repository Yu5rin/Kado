using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

public class TaskEditorViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>タスクリスト欄の候補。</summary>
    private static readonly SourceChoice[] TaskLists = [new("local:mytasks", "マイタスク")];

    [Fact]
    public void タイトルが空なら保存できない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24));

        Assert.False(vm.CanSave);

        vm.Title = "台数計画の確定";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void 期限を外せる()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24)) { Title = "いつかやる" };

        Assert.Equal(D(2026, 9, 24), vm.ToModel().Due);

        vm.HasDue = false;

        // 「期限だけある」「いつやるか未定」を持てる必要がある（要件書 3.1）
        Assert.Null(vm.ToModel().Due);
    }

    [Fact]
    public void 期限を外しても日付は覚えている()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), [], D(2026, 9, 24)) { Title = "提出" };

        vm.HasDue = false;
        vm.HasDue = true;

        Assert.Equal(D(2026, 9, 24), vm.ToModel().Due);
    }

    // ------------------------------------------------------------------
    // タスクリストの既定入れ先（項目2）
    // ------------------------------------------------------------------

    [Fact]
    public void 既定を渡さなければ一覧の先頭に入る()
    {
        var lists = new SourceChoice[] { new("local:mytasks", "マイタスク"), new("g1", "Google のリスト") };
        var vm = new TaskEditorViewModel(D(2026, 9, 24), lists, D(2026, 9, 24));

        Assert.Equal("local:mytasks", vm.TaskListId);
    }

    [Fact]
    public void 既定の入れ先が渡されればそちらを優先する()
    {
        // ローカルが先頭でも、呼び出し側（SourceListsViewModel.DefaultTaskList）が
        // 決めた入れ先を優先する。渡さないと同期対象外のローカル固定になる
        var lists = new SourceChoice[] { new("local:mytasks", "マイタスク"), new("g1", "Google のリスト") };
        var vm = new TaskEditorViewModel(D(2026, 9, 24), lists, D(2026, 9, 24), defaultTaskListId: "g1");

        Assert.Equal("g1", vm.TaskListId);
    }

    [Fact]
    public void 期限の無いタスクを開くと今日が入る()
    {
        var source = new TaskItem { Id = "t1", Title = "未定", Due = null };
        var vm = new TaskEditorViewModel(source, [], today: D(2026, 9, 24));

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

        var vm = new TaskEditorViewModel(source, TaskLists, D(2026, 9, 24)) { Title = "集計（変更）" };
        var model = vm.ToModel();

        Assert.Equal("t1", model.Id);
        Assert.Equal("g1", model.GoogleTaskId);
        Assert.Equal("gl1", model.GoogleTaskListId);
        Assert.Equal("google", model.Source);
    }

    [Fact]
    public void 期限の早入れが使える()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), [], today: D(2026, 9, 24)) { Title = "提出" };

        Assert.Equal(["今日", "明日", "来週"], vm.DuePresets.Select(p => p.Label));
        Assert.Equal(D(2026, 9, 25), vm.DuePresets[1].Date);

        vm.SetDue(vm.DuePresets[2].Date);
        Assert.Equal(D(2026, 10, 1), vm.ToModel().Due);
    }

    [Fact]
    public void 早入れは期限なしのタスクにも期限を付ける()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), [], today: D(2026, 9, 24))
        {
            Title = "いつかやる",
            HasDue = false,
        };

        vm.SetDue(D(2026, 9, 30));

        Assert.True(vm.HasDue);
        Assert.Equal(D(2026, 9, 30), vm.ToModel().Due);
    }

    [Fact]
    public void 長すぎるタイトルは保存できない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24))
        {
            Title = new string('あ', 1025),
        };

        Assert.False(vm.CanSave);
        Assert.Contains("タイトル", vm.ValidationMessage);
    }

    [Fact]
    public void 長すぎる詳細は保存できない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24))
        {
            Title = "集計",
            Note = new string('あ', 8193),
        };

        Assert.False(vm.CanSave);
        Assert.Contains("詳細", vm.ValidationMessage);
    }

    [Fact]
    public void 新規作成では削除を求めても効かない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24));
        vm.RequestDelete();

        Assert.False(vm.Deleted);
    }

    [Fact]
    public void 既存のタスクは削除を求められる()
    {
        var source = new TaskItem { Id = "t1", Title = "集計", Due = D(2026, 9, 24) };
        var vm = new TaskEditorViewModel(source, TaskLists, D(2026, 9, 24));

        Assert.False(vm.Deleted);
        vm.RequestDelete();

        Assert.True(vm.Deleted);
    }

    // ------------------------------------------------------------------
    // 完了日時（項目3）
    //
    // 完了日時が無いと「N実働日 遅れて完了」が出ない。画面から完了にしたときも
    // 入れる必要がある
    // ------------------------------------------------------------------

    [Fact]
    public void 完了にすると完了日時が入る()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24))
        {
            Title = "提出", IsDone = true,
        };

        Assert.NotNull(vm.ToModel().CompletedAt);
    }

    [Fact]
    public void 完了を外すと完了日時も消える()
    {
        var source = new TaskItem
        {
            Id = "t1", Title = "提出", Due = D(2026, 9, 20), IsDone = true,
            CompletedAt = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.FromHours(9)),
        };
        var vm = new TaskEditorViewModel(source, TaskLists, D(2026, 9, 24)) { IsDone = false };

        Assert.Null(vm.ToModel().CompletedAt);
    }

    [Fact]
    public void 元々あった完了日時は上書きしない()
    {
        // Google から来た完了日時を、こちらで保存し直すたびに今の時刻へ書き換えない
        var original = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.FromHours(9));
        var source = new TaskItem
        {
            Id = "t1", Title = "提出", Due = D(2026, 9, 20), IsDone = true, CompletedAt = original,
        };
        var vm = new TaskEditorViewModel(source, TaskLists, D(2026, 9, 24)) { Title = "提出（変更）" };

        Assert.Equal(original, vm.ToModel().CompletedAt);
    }

    [Fact]
    public void 完了していなければ完了日時は入らない()
    {
        var vm = new TaskEditorViewModel(D(2026, 9, 24), TaskLists, D(2026, 9, 24)) { Title = "提出" };

        Assert.Null(vm.ToModel().CompletedAt);
    }
}
