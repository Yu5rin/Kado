using Dapper;
using Kado.Data.Migrations;
using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// 「Google から外れた」タスクリストの印（V11）。カレンダーの印（V10）と同じ扱い。
/// </summary>
public class TaskListDetachedTests
{
    private static TaskListSource List(string id, string title) => new()
    {
        Id = id, Title = title, GoogleRaw = "{\"id\":\"" + id + "\"}", UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void V11は既存のタスクリストに印を立てずに列を足す()
    {
        using var db = TestDatabase.CreateWithoutSchema();

        foreach (var migration in SchemaMigrations.All.Where(m => m.Version < 11).OrderBy(m => m.Version))
        {
            db.Connection.Execute(migration.Sql);
            db.Connection.Execute($"PRAGMA user_version = {migration.Version};");
        }

        db.Connection.Execute("INSERT INTO task_lists (id, title, updated_at) VALUES ('list-a', '仕事', 1);");

        var applied = DatabaseMigrator.Migrate(db.Connection);

        Assert.Contains(applied, m => m.Version == 11);
        Assert.Equal(0L, db.Connection.ExecuteScalar<long>(
            "SELECT google_detached FROM task_lists WHERE id = 'list-a';"));
    }

    [Fact]
    public void 外れた印は取り込み直しでは戻らない()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);

        repository.Upsert(List("a", "仕事"));
        Assert.False(repository.FindTaskList("a")!.IsDetached);

        repository.SetTaskListDetached("a", true);

        // 一覧の取り込み（Upsert）が印を消すと、止めたはずの同期がまた動き出す
        repository.Upsert(List("a", "仕事（改名）"));

        Assert.True(repository.FindTaskList("a")!.IsDetached);

        repository.SetTaskListDetached("a", false);
        Assert.False(repository.FindTaskList("a")!.IsDetached);
    }

    [Fact]
    public void 外れたタスクリストを中のタスクごと片付けられる()
    {
        using var db = TestDatabase.Create();
        var repository = new SourceRepository(db.Connection);
        var tasks = new TaskRepository(db.Connection);

        repository.Upsert(List("a", "仕事"));
        repository.Upsert(List("b", "私用"));
        tasks.Upsert(new TaskItem { Id = "t1", Title = "仕事のタスク", TaskListId = "a", UpdatedAt = DateTimeOffset.Now });
        tasks.Upsert(new TaskItem { Id = "t2", Title = "私用のタスク", TaskListId = "b", UpdatedAt = DateTimeOffset.Now });

        Assert.Equal(1, repository.DropRemovedTaskList("a"));

        Assert.Null(repository.FindTaskList("a"));
        Assert.Equal(["t2"], tasks.All().Select(t => t.Id).ToArray());
    }
}
