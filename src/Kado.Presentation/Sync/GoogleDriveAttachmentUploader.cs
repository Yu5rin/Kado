using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;
using Kado.Presentation.Editing;
using Kado.Presentation.Net;

namespace Kado.Presentation.Sync;

/// <summary>
/// 添付を Google ドライブへ上げる、実物の実装。
/// <para>
/// 上げ先はドライブに作った「Kado」フォルダの中（要件書どおり）。<c>drive.file</c> 権限では
/// 自分で作ったフォルダしか見えないので、フォルダ ID は一度作ったら
/// <see cref="SettingsRepository"/> に控えて使い回す。フォルダが消されていたら
/// （利用者がドライブ側で消したなど）、控えを捨てて作り直す。
/// </para>
/// </summary>
public sealed class GoogleDriveAttachmentUploader(
    IGoogleSync google, SettingsRepository settings, NetworkLog? log = null)
    : IAttachmentUploader
{
    private readonly NetworkLog _log = log ?? NetworkLog.None;

    /// <summary>控えておくフォルダ ID の設定キー。</summary>
    public const string FolderIdKey = "google:drive_attachment_folder_id";

    /// <summary>作るフォルダの名前。</summary>
    public const string FolderName = "Kado";

    /// <summary>
    /// 上げる。<b>失敗はすべて理由の文言にして返し、例外は投げない</b>（取り消しだけは別）。
    /// <para>
    /// 呼び出しは画面の <c>async void</c> で、ここから漏れた例外はアプリごと終わらせる。
    /// 認可（トークンの更新・ブラウザの起動・localhost の受け口）、通信、応答の読み取り、
    /// ファイルの読み取り、フォルダ ID の控え（設定の書き込み）のどこで転んでも、
    /// <see cref="AttachmentFailure.Describe"/> で文言にする。
    /// </para>
    /// <para>
    /// <b>待ち合わせのあとは、呼ばれた場所へ戻る（<c>ConfigureAwait(true)</c>）。</b>
    /// フォルダ ID の控えは画面側の接続（<c>settings</c>）に読み書きする。接続は呼んだ
    /// スレッドで使うものなので、<c>ConfigureAwait(false)</c> で別のスレッドへ流れたまま
    /// 触らない。
    /// </para>
    /// <para>
    /// <b>通信の始まりは <c>Task.Run</c> の中。</b>最初の HTTP 要求は、経路（プロキシ）の自動検出で
    /// 呼んだスレッドのまま数秒止まることがある。画面のスレッドで始めない。通信を包むのは
    /// 通信の呼び出しだけで、設定（画面側の DB）の読み書きは包まない（画面のスレッドで行う）。
    /// </para>
    /// <para>
    /// 失敗は shell.log に1行で残す（種類・例外の連鎖）。
    /// </para>
    /// </summary>
    public async Task<AttachmentUploadResult> UploadAsync(
        string localFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        try
        {
            return await UploadCoreAsync(localFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (!AttachmentFailure.IsCancellation(ex, cancellationToken)
                                   && ex is not OutOfMemoryException)
        {
            _log.Failure("Google 添付", ex, GoogleFailure.Classify(ex), "アップロード");

            return AttachmentUploadResult.Failure(AttachmentFailure.Describe(ex));
        }
    }

    private async Task<AttachmentUploadResult> UploadCoreAsync(
        string localFilePath, CancellationToken cancellationToken)
    {
        if (!google.IsConnected)
        {
            return AttachmentUploadResult.Failure("Google に接続していません。");
        }

        if (!google.HasDriveAttachmentScope)
        {
            var granted = await google.EnsureDriveAttachmentScopeAsync(cancellationToken).ConfigureAwait(true);

            if (!granted)
            {
                return AttachmentUploadResult.Failure(
                    "添付を足すにはドライブの権限が要ります。次に試すときにもう一度確認画面が出ます。");
            }
        }

        var api = google.CreateDriveApi();

        try
        {
            var folderId = await EnsureFolderAsync(api, cancellationToken).ConfigureAwait(true);
            var uploaded = await UploadOnceAsync(api, folderId, localFilePath, cancellationToken)
                .ConfigureAwait(true);

            return AttachmentUploadResult.Success(uploaded);
        }
        catch (GoogleApiException ex) when (ex.IsMissing)
        {
            // 控えていたフォルダが無い（利用者がドライブ側で消したなど）。控えを捨てて
            // 作り直し、1回だけやり直す。やり直しの失敗は、呼び出し元の UploadAsync が文言にする
            settings.Remove(FolderIdKey);

            var recreated = await EnsureFolderAsync(api, cancellationToken).ConfigureAwait(true);
            var uploaded = await UploadOnceAsync(api, recreated, localFilePath, cancellationToken)
                .ConfigureAwait(true);

            return AttachmentUploadResult.Success(uploaded);
        }
    }

    private async Task<EventAttachment> UploadOnceAsync(
        GoogleDriveApi api, string folderId, string localFilePath, CancellationToken cancellationToken)
    {
        var mimeType = AttachmentMimeTypes.GuessFrom(localFilePath);

        // 通信の始まりは別のスレッドで（上の UploadAsync の説明）。戻ってから画面側の接続に触る
        var uploaded = await Task
            .Run(() =>
            {
                LogRouteOnce();
                return api.UploadFileAsync(folderId, localFilePath, mimeType, cancellationToken);
            })
            .ConfigureAwait(true);

        var fileId = ReadId(uploaded);

        var fileUrl = uploaded.TryGetProperty("webViewLink", out var link) && link.GetString() is { Length: > 0 } url
            ? url
            : $"https://drive.google.com/file/d/{fileId}/view";

        var title = uploaded.TryGetProperty("name", out var name)
            ? name.GetString()
            : Path.GetFileName(localFilePath);

        var iconLink = uploaded.TryGetProperty("iconLink", out var icon) ? icon.GetString() : null;

        return new EventAttachment(fileId, fileUrl, title, mimeType, iconLink);
    }

    /// <summary>経路（プロキシ）を記録する。調べる処理が数秒止まることがあるので、通信と同じく別のスレッドで呼ぶ。</summary>
    private void LogRouteOnce() => _log.LogProxyOnce("Google 通信", "https://www.googleapis.com/");

    private async Task<string> EnsureFolderAsync(GoogleDriveApi api, CancellationToken cancellationToken)
    {
        if (settings.Get(FolderIdKey) is { Length: > 0 } cached) return cached;

        var created = await Task
            .Run(() =>
            {
                LogRouteOnce();
                return api.CreateFolderAsync(FolderName, cancellationToken);
            })
            .ConfigureAwait(true);
        var id = ReadId(created);

        settings.Set(FolderIdKey, id);
        return id;
    }

    /// <summary>
    /// 応答から id を取る。無ければ <see cref="InvalidOperationException"/>。
    /// <para>
    /// 空の応答（<c>default</c> の <see cref="System.Text.Json.JsonElement"/>）や、id の無い応答に
    /// <c>GetProperty</c> を当てると、型の違う例外がばらばらに漏れる。ここで1つにそろえる。
    /// </para>
    /// </summary>
    private static string ReadId(System.Text.Json.JsonElement body)
    {
        if (body.ValueKind == System.Text.Json.JsonValueKind.Object
            && body.TryGetProperty("id", out var id)
            && id.ValueKind == System.Text.Json.JsonValueKind.String
            && id.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw new InvalidOperationException("応答に id がありません");
    }
}
