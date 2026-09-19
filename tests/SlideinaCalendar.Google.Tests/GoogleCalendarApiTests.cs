using System.Net;
using SlideinaCalendar.Google.Sync;

namespace SlideinaCalendar.Google.Tests;

/// <summary>
/// Google Calendar API の呼び出し。
/// <para>実際には繋がず、決まった応答を返す HTTP で確かめる。</para>
/// </summary>
public class GoogleCalendarApiTests
{
    /// <summary>いつでも同じトークンを返す。</summary>
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private static (GoogleCalendarApi Api, List<HttpRequestMessage> Seen) Create(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler(request =>
        {
            seen.Add(request);
            return respond(request);
        });

        return (new GoogleCalendarApi(new HttpClient(handler), new FixedToken()), seen);
    }

    [Fact]
    public async Task 一覧を読める()
    {
        var (api, _) = Create(_ => (HttpStatusCode.OK, """
            {
              "items": [
                {"id":"e1","summary":"定例"},
                {"id":"e2","summary":"棚卸し"}
              ],
              "nextSyncToken": "CAESB..."
            }
            """));

        var page = await api.ListEventsAsync("primary");

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("CAESB...", page.NextSyncToken);
        Assert.Null(page.NextPageToken);
    }

    [Fact]
    public async Task 空の一覧でも落ちない()
    {
        var (api, _) = Create(_ => (HttpStatusCode.OK, """{"nextSyncToken":"CAESB..."}"""));

        var page = await api.ListEventsAsync("primary");

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task アクセストークンを添える()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListEventsAsync("primary");

        Assert.Equal("Bearer", seen[0].Headers.Authorization!.Scheme);
        Assert.Equal("ya29.test", seen[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task 差分のときはsyncTokenを付ける()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListEventsAsync("primary", syncToken: "CAESB...");

        var url = seen[0].RequestUri!.ToString();
        Assert.Contains("syncToken=CAESB", url, StringComparison.Ordinal);

        // 差分に timeMin を付けると 400 になる
        Assert.DoesNotContain("timeMin", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 初回は期間を切る()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));

        await api.ListEventsAsync("primary", from: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // 全履歴は要らない
        Assert.Contains("timeMin", seen[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 古いsyncTokenは全再同期を求める()
    {
        var (api, _) = Create(_ => (HttpStatusCode.Gone, """
            {"error":{"code":410,"errors":[{"reason":"fullSyncRequired"}],"message":"Sync token is no longer valid"}}
            """));

        var error = await Assert.ThrowsAsync<GoogleApiException>(
            () => api.ListEventsAsync("primary", syncToken: "furui"));

        // token を捨てて全部取り直す合図
        Assert.True(error.NeedsFullResync);
        Assert.Equal("fullSyncRequired", error.Reason);
    }

    [Fact]
    public async Task 呼びすぎを見分けられる()
    {
        var (api, _) = Create(_ => (HttpStatusCode.Forbidden, """
            {"error":{"errors":[{"reason":"rateLimitExceeded"}],"message":"Rate Limit Exceeded"}}
            """));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.ListEventsAsync("primary"));

        Assert.True(error.IsRateLimited);
        Assert.True(error.IsTransient);
        Assert.False(error.NeedsFullResync);
    }

    [Fact]
    public async Task 一時的な失敗と本当の拒否を分ける()
    {
        var (unavailable, _) = Create(_ => (HttpStatusCode.ServiceUnavailable, "{}"));
        var error = await Assert.ThrowsAsync<GoogleApiException>(() => unavailable.ListEventsAsync("primary"));
        Assert.True(error.IsTransient);

        // 権限不足は出し直しても直らない
        var (forbidden, _) = Create(_ => (HttpStatusCode.Forbidden, """
            {"error":{"errors":[{"reason":"insufficientPermissions"}]}}
            """));
        var denied = await Assert.ThrowsAsync<GoogleApiException>(() => forbidden.ListEventsAsync("primary"));
        Assert.False(denied.IsTransient);
    }

    [Fact]
    public async Task 書き換えはpatchで送る()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"id":"e1","summary":"定例（変更）"}"""));

        await api.PatchEventAsync("primary", "e1", new System.Text.Json.Nodes.JsonObject
        {
            ["summary"] = "定例（変更）",
        });

        // PUT だと本文に無い項目が消える。ゲストや通知まで巻き添えになる
        Assert.Equal(HttpMethod.Patch, seen[0].Method);
        Assert.EndsWith("/calendars/primary/events/e1", seen[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 作成はPOSTで送る()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"id":"e9"}"""));

        await api.InsertEventAsync("primary", new System.Text.Json.Nodes.JsonObject { ["summary"] = "新規" });

        Assert.Equal(HttpMethod.Post, seen[0].Method);
    }

    [Fact]
    public async Task すでに無いものを消しても成功にする()
    {
        var (api, _) = Create(_ => (HttpStatusCode.NotFound, """{"error":{"code":404}}"""));

        // 消したいのだから、無いのは望む状態
        await api.DeleteEventAsync("primary", "kieta");
    }

    [Fact]
    public async Task 消せなかったら知らせる()
    {
        var (api, _) = Create(_ => (HttpStatusCode.Forbidden, """
            {"error":{"errors":[{"reason":"insufficientPermissions"}]}}
            """));

        await Assert.ThrowsAsync<GoogleApiException>(() => api.DeleteEventAsync("primary", "e1"));
    }

    [Fact]
    public async Task 続きがあれば次のページを知らせる()
    {
        var (api, _) = Create(_ => (HttpStatusCode.OK, """
            {"items":[{"id":"e1"}],"nextPageToken":"tsugi"}
            """));

        var page = await api.ListEventsAsync("primary");

        Assert.Equal("tsugi", page.NextPageToken);

        // 途中のページには syncToken が付かない
        Assert.Null(page.NextSyncToken);
    }

    [Fact]
    public async Task カレンダー一覧を読める()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """
            {"items":[{"id":"primary","summary":"仕事","backgroundColor":"#2f6fed"}]}
            """));

        var page = await api.ListCalendarsAsync();

        Assert.Single(page.Items);
        Assert.Contains("calendarList", seen[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }
}
