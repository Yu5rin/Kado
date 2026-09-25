using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kado.Google.Sync;

/// <summary>
/// Google ドライブ API の呼び出し。添付を上げるためだけに使う。
/// <para>
/// 求める権限は <c>drive.file</c> だけ（要件書どおり、必要最小限）。この権限では、
/// <b>こちらが作った・こちらで開いたファイルしか見えない</b>。利用者が持っている
/// 既存のファイルを一覧したり検索したりはできないし、しない。アップロード先の
/// 「Kado」フォルダも自分で作る（<see cref="CreateFolderAsync"/>）。
/// </para>
/// </summary>
public sealed class GoogleDriveApi(HttpClient http, IAccessTokenSource tokens)
{
    private const string Root = "https://www.googleapis.com/drive/v3";
    private const string UploadRoot = "https://www.googleapis.com/upload/drive/v3/files";

    /// <summary>フォルダを表す MIME 種別。</summary>
    public const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IAccessTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>
    /// フォルダを作る。
    /// <para>共有設定はドライブの既定のまま。こちらから変えない。</para>
    /// </summary>
    /// <returns>作ったフォルダ。<c>id</c> を持つ。</returns>
    public async Task<JsonElement> CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var body = new JsonObject { ["name"] = name, ["mimeType"] = FolderMimeType };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/files?fields=id,name")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ファイルを1つ、指定したフォルダへ上げる（マルチパート）。
    /// <para>
    /// レジューム可能アップロードは、途切れたときに途中から続けられる利点があるが、
    /// 編集画面で1件ずつ選んで足す使い方では効果が薄く、実装も複雑になる。ここでは
    /// 1回で送るマルチパートにしている。
    /// </para>
    /// </summary>
    /// <returns>上がったファイル。<c>id</c>・<c>webViewLink</c> などを持つ。</returns>
    public async Task<JsonElement> UploadFileAsync(
        string folderId, string localFilePath, string? mimeType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        var metadata = new JsonObject
        {
            ["name"] = Path.GetFileName(localFilePath),
            ["parents"] = new JsonArray(folderId),
        };

        using var content = new MultipartContent("related");
        content.Add(new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json"));

        await using var stream = File.OpenRead(localFilePath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            mimeType is { Length: > 0 } ? mimeType : "application/octet-stream");
        content.Add(fileContent);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{UploadRoot}?uploadType=multipart&fields=id,name,mimeType,webViewLink,iconLink")
        {
            Content = content,
        };

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text)) return default;

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new GoogleApiException(response.StatusCode, ReadReason(body), body);
    }

    private static string ReadReason(string body)
    {
        try
        {
            var error = JsonDocument.Parse(body).RootElement.GetProperty("error");

            if (error.TryGetProperty("errors", out var list) &&
                list.ValueKind == JsonValueKind.Array &&
                list.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first &&
                Mapping.GoogleJson.Text(first, "reason") is { } reason)
            {
                return reason;
            }

            return Mapping.GoogleJson.Text(error, "message") ?? "理由なし";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "理由なし";
        }
    }
}
