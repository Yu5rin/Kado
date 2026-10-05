using Kado.Data.Models;
using Kado.Presentation.Editing;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクの編集画面の、繰り返し。<b>Kado だけが持つ項目</b>で、Google には送らない。
/// 期限の無いタスクとサブタスクには付けられない。
/// </summary>
public class TaskEditorRepeatTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly SourceChoice[] TaskLists = [new("local:mytasks", "マイタスク")];

    /// <summary>2026/9/24 は木曜。</summary>
    private static TaskEditorViewModel NewTask(DateOnly? due = null) =>
        new(due ?? D(2026, 9, 24), TaskLists, D(2026, 9, 24)) { Title = "週報" };

    private static TaskEditorViewModel Edit(TaskItem value) =>
        new(value, TaskLists, D(2026, 9, 24));

    private static TaskItem Existing(string? repeat = null, DateOnly? due = null, string? parentId = null) => new()
    {
        Id = "t1", Title = "週報", Due = due ?? D(2026, 9, 24), TaskListId = "local:mytasks",
        Repeat = repeat, ParentId = parentId,
    };

    // ------------------------------------------------------------------
    // 選択肢
    // ------------------------------------------------------------------

    [Fact]
    public void 選択肢の文言は期限日に合わせて具体的に出る()
    {
        var vm = NewTask();

        Assert.Equal(
            [
                "繰り返さない",
                "毎日",
                "毎週 木曜日",
                "毎月 24日",
                "毎年 9月24日",
                "毎週の週始め（最初の稼働日）",
                "毎週の週終わり（最後の稼働日）",
                "毎月の月初（最初の稼働日）",
                "毎月の月末（最後の稼働日）",
            ],
            vm.RepeatOptions.Select(o => o.Label));
    }

    [Fact]
    public void 末日や閏日の期限でも文言はその日付のまま()
    {
        var vm = NewTask(D(2028, 2, 29));

        Assert.Contains(vm.RepeatOptions, o => o.Label == "毎月 29日");
        Assert.Contains(vm.RepeatOptions, o => o.Label == "毎年 2月29日");

        vm.Due = D(2026, 10, 31);

        Assert.Contains(vm.RepeatOptions, o => o.Label == "毎月 31日");
        Assert.Contains(vm.RepeatOptions, o => o.Label == "毎週 土曜日");
    }

    [Fact]
    public void 初期値は繰り返さない()
    {
        var vm = NewTask();

        Assert.Equal(TaskRepeatChoice.None, vm.RepeatKey);
        Assert.Null(vm.ToModel().Repeat);
    }

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Daily", "FREQ=DAILY")]
    [InlineData("Weekly", "FREQ=WEEKLY;BYDAY=TH")]
    [InlineData("Monthly", "FREQ=MONTHLY;BYMONTHDAY=24")]
    [InlineData("Yearly", "FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=24")]
    [InlineData("WeekFirstWorkday", "X-KADO=WEEK-FIRST-WORKDAY")]
    [InlineData("WeekLastWorkday", "X-KADO=WEEK-LAST-WORKDAY")]
    [InlineData("MonthFirstWorkday", "X-KADO=MONTH-FIRST-WORKDAY")]
    [InlineData("MonthLastWorkday", "X-KADO=MONTH-LAST-WORKDAY")]
    public void 選んだ繰り返しが指定文字列になる(string key, string expected)
    {
        var vm = NewTask();

        vm.RepeatKey = key;

        Assert.Equal(expected, vm.ToModel().Repeat);
    }

    [Fact]
    public void 期限日を変えたら暦どおりの指定を新しい期限日で指定し直す()
    {
        var vm = NewTask();
        vm.RepeatKey = "Weekly";

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Due = D(2026, 9, 25);

        // 文言が変わるので選択肢を出し直す
        Assert.Contains(nameof(TaskEditorViewModel.RepeatOptions), changed);
        Assert.Contains(vm.RepeatOptions, o => o.Label == "毎週 金曜日");

        // 選んでいる種類は保ったまま、指定は新しい期限日の曜日になる
        Assert.Equal("Weekly", vm.RepeatKey);
        Assert.Equal("FREQ=WEEKLY;BYDAY=FR", vm.ToModel().Repeat);
    }

    [Fact]
    public void 早入れで期限を変えても指定が追従する()
    {
        var vm = NewTask();
        vm.RepeatKey = "Monthly";

        vm.SetDue(D(2026, 10, 31));

        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=31", vm.ToModel().Repeat);
    }

    [Fact]
    public void 稼働日基準は期限日を変えても指定が変わらない()
    {
        var vm = NewTask();
        vm.RepeatKey = "MonthLastWorkday";

        vm.Due = D(2026, 12, 1);

        Assert.Equal("X-KADO=MONTH-LAST-WORKDAY", vm.ToModel().Repeat);
    }

    [Fact]
    public void 開いたタスクの繰り返しが選ばれている()
    {
        Assert.Equal("Weekly", Edit(Existing("FREQ=WEEKLY;BYDAY=TH")).RepeatKey);
        Assert.Equal("MonthLastWorkday", Edit(Existing("X-KADO=MONTH-LAST-WORKDAY")).RepeatKey);
        Assert.Equal(TaskRepeatChoice.None, Edit(Existing(null)).RepeatKey);
    }

    [Fact]
    public void 触らずに保存しても繰り返しは変わらない()
    {
        var vm = Edit(Existing("FREQ=MONTHLY;BYMONTHDAY=24"));

        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=24", vm.ToModel().Repeat);
    }

    [Fact]
    public void 繰り返さないに戻して保存すると外れる()
    {
        var vm = Edit(Existing("FREQ=DAILY"));

        vm.RepeatKey = TaskRepeatChoice.None;

        Assert.Null(vm.ToModel().Repeat);
    }

    [Fact]
    public void 選択肢の差し替えで来るnullは選びなおしとして扱わない()
    {
        var vm = NewTask();
        vm.RepeatKey = "Daily";

        vm.RepeatKey = null!;

        Assert.Equal("Daily", vm.RepeatKey);
    }

    // ------------------------------------------------------------------
    // 付けられない条件
    // ------------------------------------------------------------------

    [Fact]
    public void 期限を外すと繰り返しは選べず保存でも外れ理由を出す()
    {
        var vm = NewTask();
        vm.RepeatKey = "Weekly";
        Assert.True(vm.CanRepeat);
        Assert.Null(vm.RepeatLockReason);

        vm.HasDue = false;

        Assert.False(vm.CanRepeat);
        Assert.Equal("繰り返しは、期限を付けると選べます", vm.RepeatLockReason);
        Assert.Null(vm.ToModel().Repeat);

        // 期限を付け直せば、選んでいた繰り返しが戻る
        vm.HasDue = true;

        Assert.True(vm.CanRepeat);
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH", vm.ToModel().Repeat);
    }

    [Fact]
    public void 期限の状態が変わると選べるかの表示も出し直す()
    {
        var vm = NewTask();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.HasDue = false;

        Assert.Contains(nameof(TaskEditorViewModel.CanRepeat), changed);
        Assert.Contains(nameof(TaskEditorViewModel.RepeatLockReason), changed);
    }

    [Fact]
    public void 既存の期限を消して保存すると繰り返しも外れる()
    {
        var vm = Edit(Existing("FREQ=WEEKLY;BYDAY=TH"));

        vm.HasDue = false;

        var model = vm.ToModel();
        Assert.Null(model.Due);
        Assert.Null(model.Repeat);
    }

    [Fact]
    public void サブタスクには繰り返しを選べず保存でも外れる()
    {
        var vm = Edit(Existing("FREQ=WEEKLY;BYDAY=TH", parentId: "parent"));

        Assert.False(vm.CanRepeat);
        Assert.Equal("サブタスクには繰り返しを付けられません", vm.RepeatLockReason);
        Assert.Null(vm.ToModel().Repeat);
    }

    // ------------------------------------------------------------------
    // 読めない指定
    // ------------------------------------------------------------------

    [Fact]
    public void 読めない指定は選択肢に出して消さずに持ち続ける()
    {
        var vm = Edit(Existing("こわれた文字列"));

        Assert.Equal(TaskRepeatChoice.Custom, vm.RepeatKey);
        Assert.Contains(vm.RepeatOptions, o => o.Key == TaskRepeatChoice.Custom && o.Label == "読めない指定のまま（繰り返しません）");

        // 題名を直して保存し直しただけでは、文字列を失わない
        Assert.Equal("こわれた文字列", vm.ToModel().Repeat);

        // 自分で選び直したら、そちらになる
        vm.RepeatKey = "Daily";
        Assert.Equal("FREQ=DAILY", vm.ToModel().Repeat);
    }

    [Fact]
    public void 読めない指定の選択肢は読める指定のときは出ない()
    {
        Assert.DoesNotContain(Edit(Existing("FREQ=DAILY")).RepeatOptions, o => o.Key == TaskRepeatChoice.Custom);
        Assert.DoesNotContain(NewTask().RepeatOptions, o => o.Key == TaskRepeatChoice.Custom);
    }

    // ------------------------------------------------------------------
    // 注意書き
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返しはKadoだけで動くことを小さく伝える()
    {
        Assert.Equal(
            "繰り返しは Kado だけで動きます。Google のアプリで同じタスクに繰り返しを付けると二重になります",
            NewTask().RepeatNote);
    }
}
