using System.Net;
using System.Text.Json.Nodes;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// Google Tasks API の呼び出し。
/// <para>Calendar と違って syncToken が無く、差分は updatedMin で取る。</para>
/// </summary>
public class GoogleTasksApiTests
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private static (GoogleTasksApi Api, List<HttpRequestMessage> Seen) Create(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler(request =>
        {
            seen.Add(request);
            return respond(request);
        });

        return (new GoogleTasksApi(new HttpClient(handler), new FixedToken()), seen);
    }

    [Fact]
    public async Task 一覧を読める()
    {
        var (api, _) = Create(_ => (HttpStatusCode.OK, """
            {"items":[{"id":"t1","title":"集計"},{"id":"t2","title":"提出"}]}
            """));

        var page = await api.ListTasksAsync("@default");

        Assert.Equal(2, page.Items.Count);

        // Tasks は syncToken を返さない
        Assert.Null(page.NextSyncToken);
    }

    [Fact]
    public async Task 消えたものと完了したものも取りに行く()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListTasksAsync("@default");

        var url = seen[0].RequestUri!.ToString();

        // 立てないと、こちらに残り続ける
        Assert.Contains("showDeleted=true", url, StringComparison.Ordinal);
        Assert.Contains("showHidden=true", url, StringComparison.Ordinal);
        Assert.Contains("showCompleted=true", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 差分は少し前から取り直す()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        var since = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        await api.ListTasksAsync("@default", updatedSince: since);

        var url = Uri.UnescapeDataString(seen[0].RequestUri!.ToString());

        // 時計のずれで取りこぼすより、同じものを二度読むほうが安い
        Assert.Contains("updatedMin=", url, StringComparison.Ordinal);
        Assert.Contains((since - GoogleTasksApi.Overlap).ToString("O"), url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 差分でなければupdatedMinを付けない()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListTasksAsync("@default");

        Assert.DoesNotContain("updatedMin", seen[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 書き換えはpatchで送る()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"id":"t1"}"""));

        await api.PatchTaskAsync("@default", "t1", new JsonObject { ["title"] = "集計（変更）" });

        Assert.Equal(HttpMethod.Patch, seen[0].Method);

        // 識別子は URL に入れる前に逃がす。カレンダー ID はメールアドレスの形で来る
        Assert.EndsWith(
            "/lists/@default/tasks/t1",
            Uri.UnescapeDataString(seen[0].RequestUri!.AbsolutePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task すでに無いものを消しても成功にする()
    {
        var (api, _) = Create(_ => (HttpStatusCode.NotFound, """{"error":{"code":404}}"""));

        await api.DeleteTaskAsync("@default", "kieta");
    }

    [Fact]
    public async Task タスクリストの一覧を読める()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """
            {"items":[{"id":"MTIzNDU2","title":"マイタスク"}]}
            """));

        var page = await api.ListTaskListsAsync();

        Assert.Single(page.Items);
        Assert.Contains("users/@me/lists", seen[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 呼びすぎを見分けられる()
    {
        var (api, _) = Create(_ => (HttpStatusCode.TooManyRequests, "{}"));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListTasksAsync("@default"));

        Assert.True(error.IsRateLimited);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task アクセストークンを添える()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListTasksAsync("@default");

        Assert.Equal("ya29.test", seen[0].Headers.Authorization!.Parameter);
    }
}
