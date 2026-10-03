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
public sealed class GoogleDriveApi(
    HttpClient http, IAccessTokenSource tokens, GoogleRetryPolicy? retry = null)
{
    private const string Root = "https://www.googleapis.com/drive/v3";
    private const string UploadRoot = "https://www.googleapis.com/upload/drive/v3/files";

    /// <summary>フォルダを表す MIME 種別。</summary>
    public const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IAccessTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>429・5xx のときに待って出し直す方針。省略すると本物の待ち方。</summary>
    private readonly GoogleRetryPolicy _retry = retry ?? GoogleRetryPolicy.Default;

    /// <summary>フォルダを作るような小さい呼び出しの待ち時間。ほかの API と同じ30秒。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>アップロードの全体の上限の、最低ライン。</summary>
    public static readonly TimeSpan UploadMinimumTimeout = TimeSpan.FromMinutes(2);

    /// <summary>アップロードの全体の上限に、1MB ごとに足す長さ。およそ 34KB/s を下限の速さとみる。</summary>
    public static readonly TimeSpan UploadTimeoutPerMegabyte = TimeSpan.FromSeconds(30);

    /// <summary>アップロードの全体の上限の、いちばん長いところ。</summary>
    public static readonly TimeSpan UploadMaximumTimeout = TimeSpan.FromMinutes(30);

    /// <summary>送るのが止まったとみなす長さ。1バイトでも進めば、ここから数え直す。</summary>
    public static readonly TimeSpan DefaultUploadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 送り終えてから、Google の応答を待つ長さ。送るのが止まったとみなす長さとは別に持つ
    /// （送り終えたあとは読み取りが進まないので、止まったことにしてはいけない）。
    /// </summary>
    public static readonly TimeSpan UploadResponseTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 送るのが止まったとみなす長さ。既定は <see cref="DefaultUploadStallTimeout"/>。
    /// 試験では短くして、止まったときの動きを確かめる。
    /// </summary>
    public TimeSpan UploadStallTimeout { get; init; } = DefaultUploadStallTimeout;

    /// <summary>
    /// アップロードの全体の上限と、進みが止まったときの打ち切りが時間を取る元。既定は実時間
    /// （<see cref="TimeProvider.System"/>）。テストで時間を進めるために差し替えられる。
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// ファイルの大きさに応じたアップロード全体の上限。
    /// <para>
    /// 最低2分、1MB ごとに30秒を足し、30分で頭打ち。全体の上限だけだと、遅い回線で大きなものを
    /// 送るときに、進んでいるのに切れる。止まったかどうかは、別に進み具合で見る
    /// （<see cref="UploadStallTimeout"/>）。
    /// </para>
    /// </summary>
    public static TimeSpan UploadTimeoutFor(long sizeBytes)
    {
        var megabytes = Math.Max(0, sizeBytes) / (1024.0 * 1024.0);
        var span = UploadMinimumTimeout + TimeSpan.FromSeconds(Math.Ceiling(megabytes) * UploadTimeoutPerMegabyte.TotalSeconds);

        return span > UploadMaximumTimeout ? UploadMaximumTimeout : span;
    }

    /// <summary>
    /// フォルダを作る。
    /// <para>共有設定はドライブの既定のまま。こちらから変えない。</para>
    /// </summary>
    /// <returns>作ったフォルダ。<c>id</c> を持つ。</returns>
    public async Task<JsonElement> CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var body = new JsonObject { ["name"] = name, ["mimeType"] = FolderMimeType };

        var json = body.ToJsonString();

        using var response = await GoogleHttp.SendAsync(
            _http, _tokens,
            () => new HttpRequestMessage(HttpMethod.Post, $"{Root}/files?fields=id,name")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            _retry, RequestTimeout, cancellationToken).ConfigureAwait(false);

        await GoogleHttp.EnsureOkAsync(response, cancellationToken).ConfigureAwait(false);

        return await GoogleHttp.ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
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

        // 開けないファイル（権限が無い・消えた・フォルダだった）は、通信を始める前にここで例外にする
        long size;
        await using (var probe = File.OpenRead(localFilePath)) size = probe.Length;

        var limit = UploadTimeoutFor(size);

        // 全体の上限と、進み具合が止まったときの打ち切りと、呼び出し側の中止を、1本の待ちにする
        using var total = new CancellationTokenSource(limit, TimeProvider);
        using var stall = new CancellationTokenSource(UploadStallTimeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, total.Token, stall.Token);

        HttpRequestMessage CreateRequest()
        {
            // 出し直しのたびに、ファイルを開き直して新しい本文を作る
            stall.CancelAfter(UploadStallTimeout);

            var content = new MultipartContent("related");
            content.Add(new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json"));

            var progress = new ProgressStream(
                File.OpenRead(localFilePath),
                onProgress: () => stall.CancelAfter(UploadStallTimeout),
                // 送り終えた。応答を待つあいだは読み取りが進まないので、別の長さで待つ
                onEnd: () => stall.CancelAfter(UploadResponseTimeout));

            var fileContent = new StreamContent(progress);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                mimeType is { Length: > 0 } ? mimeType : "application/octet-stream");
            content.Add(fileContent);

            return new HttpRequestMessage(
                HttpMethod.Post,
                $"{UploadRoot}?uploadType=multipart&fields=id,name,mimeType,webViewLink,iconLink")
            {
                Content = content,
            };
        }

        try
        {
            using var response = await GoogleHttp.SendAsync(
                _http, _tokens, CreateRequest, _retry, timeout: null, linked.Token).ConfigureAwait(false);

            await GoogleHttp.EnsureOkAsync(response, linked.Token).ConfigureAwait(false);

            return await GoogleHttp.ReadBodyAsync(response, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が止めたのではない。進みが止まったか、全体の上限を超えた
            throw new TimeoutException(stall.IsCancellationRequested
                ? $"アップロードが{(int)UploadStallTimeout.TotalSeconds}秒間進まなかったので中止しました。"
                : $"アップロードが{(int)limit.TotalMinutes}分以内に終わらなかったので中止しました。");
        }
    }
}
