using System.Net;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// タスクリストが Google の一覧から消えたとき。カレンダーと同じ扱いにする。
/// <para>
/// 送っていないものが無ければ一覧から外す。あれば捨てずに残して「Google から外れた」印を付け、
/// 同期を止めて警告する。以前は何もせず、消えたリストを毎回読みに行って同期のたびに警告が出ていた。
/// </para>
/// </summary>
public class GoogleSyncTaskListGoneTests : IDisposable
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private sealed class Handler(string taskListsBody) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Seen.Add(url);

            var body = url.Contains("users/@me/lists", StringComparison.Ordinal)
                ? taskListsBody
                : """{"items":[]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose() => _test.Dispose();

    private static string Lists(params string[] ids) =>
        "{\"items\":[" + string.Join(",", ids.Select(id => $$"""{"id":"{{id}}","title":"{{id}}"}""")) + "]}";

    private void SeedList(string id)
    {
        _test.Workspace.Sources.Upsert(new TaskListSource
        {
            Id = id, Title = id, GoogleRaw = $$"""{"id":"{{id}}","title":"{{id}}"}""",
            UpdatedAt = DateTimeOffset.Now,
        });
    }

    private async Task<(SyncReport Report, Handler Handler)> SyncAsync(string listsBody)
    {
        var handler = new Handler(listsBody);
        var http = new HttpClient(handler);
        var token = new FixedToken();

        using var service = new GoogleSyncService(
            _test.Workspace, new GoogleCalendarApi(http, token), new GoogleTasksApi(http, token));

        return ((await service.SyncAsync())!, handler);
    }

    private static TaskItem NewTask(string id, string list, string? googleId) => new()
    {
        Id = id, Title = id, TaskListId = list, GoogleTaskId = googleId,
        GoogleTaskListId = googleId is null ? null : list,
        GoogleRaw = googleId is null ? null : $$"""{"id":"{{googleId}}","title":"{{id}}","status":"needsAction"}""",
        UpdatedAt = DateTimeOffset.Now,
    };

    [Fact]
    public async Task 送っていないものが無ければ一覧から外す()
    {
        SeedList("keep");
        SeedList("gone");
        _test.Workspace.Tasks.Upsert(NewTask("t1", "gone", "g1") with { Title = "t1" });

        var (report, handler) = await SyncAsync(Lists("keep"));

        Assert.Null(_test.Workspace.Sources.FindTaskList("gone"));
        Assert.DoesNotContain(_test.Workspace.Tasks.All(), t => t.TaskListId == "gone");
        Assert.True(report.SourcesChanged);
        Assert.Contains(report.Warnings, w => w.Contains("gone", StringComparison.Ordinal));

        // 消えたリストを読みに行かない
        Assert.DoesNotContain(handler.Seen, u => u.Contains("lists/gone/tasks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 送っていないタスクがあれば残して印を付け同期を止めて警告する()
    {
        SeedList("keep");
        SeedList("gone");
        _test.Workspace.Tasks.Upsert(NewTask("unsent", "gone", googleId: null));

        var (report, handler) = await SyncAsync(Lists("keep"));

        var list = _test.Workspace.Sources.FindTaskList("gone")!;
        Assert.True(list.IsDetached);
        Assert.NotNull(_test.Workspace.Tasks.Find("unsent"));
        Assert.Contains(report.Warnings, w =>
            w.Contains("gone", StringComparison.Ordinal) && w.Contains("まだ送っていない", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Seen, u => u.Contains("lists/gone/tasks", StringComparison.Ordinal));

        // 次の同期でも同じ警告を繰り返さず、読みにも行かない
        var (again, second) = await SyncAsync(Lists("keep"));
        Assert.Empty(again.Warnings);
        Assert.DoesNotContain(second.Seen, u => u.Contains("lists/gone/tasks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 編集して送っていないタスクも残す()
    {
        SeedList("keep");
        SeedList("gone");
        _test.Workspace.Tasks.Upsert(NewTask("edited", "gone", "g9") with { Title = "手元で直した" });

        await SyncAsync(Lists("keep"));

        Assert.True(_test.Workspace.Sources.FindTaskList("gone")!.IsDetached);
        Assert.Equal("手元で直した", _test.Workspace.Tasks.Find("edited")!.Title);
    }

    [Fact]
    public async Task 伝えていない削除の記録があれば残す()
    {
        SeedList("keep");
        SeedList("gone");
        _test.Workspace.Tombstones.Record(
            "deleted-task", TombstoneRepository.TaskKind, "g5", DateTimeOffset.Now, "gone");

        await SyncAsync(Lists("keep"));

        Assert.True(_test.Workspace.Sources.FindTaskList("gone")!.IsDetached);
    }

    [Fact]
    public async Task 一覧に戻ってきたら印を外して全件を取り直す()
    {
        SeedList("keep");
        SeedList("back");
        _test.Workspace.Sources.SetTaskListDetached("back", true);
        _test.Workspace.Settings.SetSyncState(TaskSyncEngine.SinceKey("back"), "2026-01-01T00:00:00.0000000+00:00");

        var (_, handler) = await SyncAsync(Lists("keep", "back"));

        Assert.False(_test.Workspace.Sources.FindTaskList("back")!.IsDetached);

        // 見ていない間の変更は差分では取れない。前回の時刻を捨てて全件を取っている（updatedMin が付かない）
        Assert.Contains(handler.Seen, u => u.Contains("lists/back/tasks", StringComparison.Ordinal)
                                           && !u.Contains("updatedMin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 一覧を1件も取れなかったときは何も外さない()
    {
        SeedList("a");

        // 応答が壊れていただけで全部消える、という壊れ方を避ける
        await SyncAsync("""{"items":[]}""");

        Assert.NotNull(_test.Workspace.Sources.FindTaskList("a"));
    }
}
