using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// タスクの繰り返し（Kado だけの項目）。保存と、タスクそのものを組み立てる決まり
/// （<see cref="TaskRepeating"/>）。次の期限の計算そのものは Kado.Core.Tests が見ている。
/// </summary>
public class TaskRepeatStorageTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static readonly DateTimeOffset Now = new(2025, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static TaskItem Weekly(DateOnly? due = null) => new()
    {
        Id = "t1", Title = "週報", Due = due ?? D(2025, 10, 6), TaskListId = "local:mytasks",
        Note = "メモ", Url = "https://example.com/spec", Attachments = """[{"path":"C:\\資料\\図面.pdf"}]""",
        Repeat = "FREQ=WEEKLY;BYDAY=MO",
        GoogleTaskId = "g1", GoogleTaskListId = "@default", GoogleRaw = "{}", GoogleUpdated = "x",
    };

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返しは書いて読める()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        // INSERT 側
        repo.Upsert(new TaskItem { Id = "t1", Title = "週報", Due = D(2025, 10, 6), Repeat = "FREQ=WEEKLY;BYDAY=MO" });
        var inserted = repo.Find("t1")!;
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", inserted.Repeat);

        // ON CONFLICT（更新）側。変えた値も、外した（null に戻した）値も書ける
        repo.Upsert(inserted with { Repeat = "X-KADO=MONTH-LAST-WORKDAY" });
        Assert.Equal("X-KADO=MONTH-LAST-WORKDAY", repo.Find("t1")!.Repeat);

        repo.Upsert(repo.Find("t1")! with { Repeat = null });
        Assert.Null(repo.Find("t1")!.Repeat);

        // 全件・期間の読み出しでも同じ列を読む
        repo.Upsert(repo.Find("t1")! with { Repeat = "FREQ=DAILY" });
        Assert.Equal("FREQ=DAILY", repo.All().Single().Repeat);
        Assert.Equal("FREQ=DAILY", repo.DueInRange(D(2025, 10, 1), D(2025, 10, 31)).Single().Repeat);
        Assert.Equal("FREQ=DAILY", repo.InRange(D(2025, 10, 1), D(2025, 10, 31)).Single().Repeat);
    }

    [Fact]
    public void 読めない指定の文字列も消さずに持つ()
    {
        using var db = TestDatabase.Create();
        var repo = new TaskRepository(db.Connection);

        repo.Upsert(new TaskItem { Id = "t1", Title = "x", Due = D(2025, 10, 6), Repeat = "こわれた文字列" });

        var stored = repo.Find("t1")!;
        Assert.Equal("こわれた文字列", stored.Repeat);
        Assert.False(stored.IsRepeating);
    }

    [Fact]
    public void 繰り返しの印は読める指定のときだけ出る()
    {
        Assert.True((new TaskItem { Repeat = "FREQ=DAILY" }).IsRepeating);
        Assert.False((new TaskItem { Repeat = null }).IsRepeating);
        Assert.False((new TaskItem { Repeat = "FREQ=HOURLY" }).IsRepeating);
    }

    // ------------------------------------------------------------------
    // 付けられない条件
    // ------------------------------------------------------------------

    [Fact]
    public void 期限が無いタスクと子タスクには繰り返しを付けられない()
    {
        Assert.True(TaskRepeating.CannotRepeat(null, null));
        Assert.True(TaskRepeating.CannotRepeat(D(2025, 10, 6), "parent"));
        Assert.False(TaskRepeating.CannotRepeat(D(2025, 10, 6), null));
    }

    [Fact]
    public void 期限を消したら繰り返しも外れる()
    {
        var saved = TaskRepeating.Normalize(Weekly() with { Due = null }, previous: Weekly());

        Assert.Null(saved.Repeat);
    }

    [Fact]
    public void 子タスクの繰り返しは外れる()
    {
        // 編集画面の写しは古いことがある。いま保存されている行の親子を信じる
        var stored = Weekly() with { ParentId = "parent" };

        Assert.Null(TaskRepeating.Normalize(Weekly(), stored).Repeat);
    }

    [Fact]
    public void 期限日が変わったら暦どおりの指定を新しい期限日で指定し直す()
    {
        // 月曜 10/6 → 水曜 10/8
        var saved = TaskRepeating.Normalize(Weekly(D(2025, 10, 8)), previous: Weekly());

        Assert.Equal("FREQ=WEEKLY;BYDAY=WE", saved.Repeat);
    }

    [Fact]
    public void 期限日が変わらなければ指定は触らない()
    {
        var saved = TaskRepeating.Normalize(Weekly() with { Title = "週報（直した）" }, previous: Weekly());

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", saved.Repeat);
    }

    [Fact]
    public void 期限日が変わっても稼働日基準と読めない指定は消さずに残す()
    {
        var workday = Weekly(D(2025, 10, 8)) with { Repeat = "X-KADO=MONTH-LAST-WORKDAY" };
        Assert.Equal("X-KADO=MONTH-LAST-WORKDAY", TaskRepeating.Normalize(workday, Weekly()).Repeat);

        var broken = Weekly(D(2025, 10, 8)) with { Repeat = "こわれた" };
        Assert.Equal("こわれた", TaskRepeating.Normalize(broken, Weekly()).Repeat);
    }

    [Fact]
    public void 繰り返しの無いタスクは何も変わらない()
    {
        var value = Weekly() with { Repeat = null, Due = null };

        Assert.Same(value, TaskRepeating.Normalize(value, Weekly()));
    }

    // ------------------------------------------------------------------
    // 次の回
    // ------------------------------------------------------------------

    [Fact]
    public void 次の回は題名メモリストURL添付と指定を引き継ぎ期限は求めた日()
    {
        var next = TaskRepeating.NextOccurrence(Weekly() with { IsDone = true }, today: D(2025, 10, 8), null, DayOfWeek.Sunday, Now)!;

        Assert.Equal(D(2025, 10, 13), next.Due);
        Assert.Equal("週報", next.Title);
        Assert.Equal("メモ", next.Note);
        Assert.Equal("local:mytasks", next.TaskListId);
        Assert.Equal("https://example.com/spec", next.Url);
        Assert.Equal("""[{"path":"C:\\資料\\図面.pdf"}]""", next.Attachments);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", next.Repeat);

        // 新しいタスク：未完了で、作成日時は今
        Assert.False(next.IsDone);
        Assert.Null(next.CompletedAt);
        Assert.Equal(Now, next.CreatedAt);
        Assert.Equal(Now, next.UpdatedAt);
    }

    [Fact]
    public void 次の回はGoogleとの結び付きを持たない()
    {
        var next = TaskRepeating.NextOccurrence(
            Weekly() with { IsDone = true, ParentId = null, Position = "0001", Source = "google" },
            D(2025, 10, 8), null, DayOfWeek.Sunday, Now)!;

        Assert.NotEqual("t1", next.Id);
        Assert.NotEmpty(next.Id);
        Assert.Null(next.GoogleTaskId);
        Assert.Null(next.GoogleTaskListId);
        Assert.Null(next.GoogleRaw);
        Assert.Null(next.GoogleUpdated);
        Assert.False(next.GoogleMissing);
        Assert.Null(next.ParentId);
        Assert.Null(next.Position);
        Assert.Null(next.Source);
    }

    [Fact]
    public void 毎月31日の次の回は指定を書き換えずに持ち越す()
    {
        var january = Weekly(D(2026, 1, 31)) with { Repeat = "FREQ=MONTHLY;BYMONTHDAY=31", IsDone = true };

        var february = TaskRepeating.NextOccurrence(january, D(2026, 1, 31), null, DayOfWeek.Sunday, Now)!;

        Assert.Equal(D(2026, 2, 28), february.Due);
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=31", february.Repeat);

        var march = TaskRepeating.NextOccurrence(february with { IsDone = true }, D(2026, 2, 28), null, DayOfWeek.Sunday, Now)!;

        Assert.Equal(D(2026, 3, 31), march.Due);
    }

    [Fact]
    public void 次の回が無いのは繰り返しが無い読めない期限が無い子タスクのとき()
    {
        TaskItem? Next(TaskItem value) =>
            TaskRepeating.NextOccurrence(value, D(2025, 10, 8), null, DayOfWeek.Sunday, Now);

        Assert.Null(Next(Weekly() with { Repeat = null }));
        Assert.Null(Next(Weekly() with { Repeat = "こわれた" }));
        Assert.Null(Next(Weekly() with { Due = null }));
        Assert.Null(Next(Weekly() with { ParentId = "parent" }));
    }

    [Fact]
    public void 完了にしたほうからは繰り返しを外す()
    {
        var completed = TaskRepeating.WithoutRepeat(Weekly() with { IsDone = true });

        Assert.Null(completed.Repeat);
        Assert.True(completed.IsDone);
        Assert.Equal("週報", completed.Title);
    }
}
