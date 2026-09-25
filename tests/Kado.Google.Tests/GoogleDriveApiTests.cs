using System.Net;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// Google ドライブ API の呼び出し。添付のアップロードにだけ使う。
/// <para>
/// <c>drive.file</c> 権限では、こちらが作ったファイル・フォルダしか見えない。
/// フォルダを検索する口は持たず、作る（<see cref="GoogleDriveApi.CreateFolderAsync"/>）
/// だけにしてある。
/// </para>
/// </summary>
public class GoogleDriveApiTests
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    private static (GoogleDriveApi Api, List<HttpRequestMessage> Seen) Create(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler(request =>
        {
            seen.Add(request);
            return respond(request);
        });

        return (new GoogleDriveApi(new HttpClient(handler), new FixedToken()), seen);
    }

    [Fact]
    public async Task フォルダを作る()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"id":"folder-1","name":"Kado"}"""));

        var folder = await api.CreateFolderAsync("Kado");

        Assert.Equal("folder-1", folder.GetProperty("id").GetString());
        Assert.Equal(HttpMethod.Post, seen[0].Method);
        Assert.Contains("/files", seen[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ファイルをマルチパートで上げる()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kado-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "内容");

        try
        {
            var (api, seen) = Create(_ => (HttpStatusCode.OK, """
                {"id":"file-1","name":"kado-test.txt","mimeType":"text/plain",
                 "webViewLink":"https://drive.google.com/file/d/file-1/view"}
                """));

            var file = await api.UploadFileAsync("folder-1", path, "text/plain");

            Assert.Equal("file-1", file.GetProperty("id").GetString());
            Assert.Equal(HttpMethod.Post, seen[0].Method);
            Assert.Contains("upload/drive/v3/files", seen[0].RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.Contains("uploadType=multipart", seen[0].RequestUri!.Query, StringComparison.Ordinal);
            Assert.Equal("multipart/related", seen[0].Content!.Headers.ContentType!.MediaType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task アクセストークンを添える()
    {
        var (api, seen) = Create(_ => (HttpStatusCode.OK, """{"id":"folder-1"}"""));

        await api.CreateFolderAsync("Kado");

        Assert.Equal("Bearer", seen[0].Headers.Authorization!.Scheme);
        Assert.Equal("ya29.test", seen[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task 断られたら理由が分かる形で投げる()
    {
        var (api, _) = Create(_ => (HttpStatusCode.Forbidden, """
            {"error":{"errors":[{"reason":"insufficientPermissions"}]}}
            """));

        var error = await Assert.ThrowsAsync<GoogleApiException>(() => api.CreateFolderAsync("Kado"));

        Assert.Equal("insufficientPermissions", error.Reason);
    }
}
