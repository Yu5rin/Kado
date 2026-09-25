using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;
using Kado.Presentation.Editing;

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
public sealed class GoogleDriveAttachmentUploader(IGoogleSync google, SettingsRepository settings)
    : IAttachmentUploader
{
    /// <summary>控えておくフォルダ ID の設定キー。</summary>
    public const string FolderIdKey = "google:drive_attachment_folder_id";

    /// <summary>作るフォルダの名前。</summary>
    public const string FolderName = "Kado";

    public async Task<AttachmentUploadResult> UploadAsync(
        string localFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        if (!google.IsConnected)
        {
            return AttachmentUploadResult.Failure("Google に接続していません。");
        }

        if (!google.HasDriveAttachmentScope)
        {
            bool granted;
            try
            {
                granted = await google.EnsureDriveAttachmentScopeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Kado.Google.OAuth.OAuthException ex)
            {
                return AttachmentUploadResult.Failure($"権限を確かめられませんでした: {ex.Message}");
            }
            catch (HttpRequestException)
            {
                return AttachmentUploadResult.Failure("通信できませんでした。オフラインの可能性があります。");
            }

            if (!granted)
            {
                return AttachmentUploadResult.Failure(
                    "添付を足すにはドライブの権限が要ります。次に試すときにもう一度確認画面が出ます。");
            }
        }

        var api = google.CreateDriveApi();

        try
        {
            var folderId = await EnsureFolderAsync(api, cancellationToken).ConfigureAwait(false);
            var uploaded = await UploadOnceAsync(api, folderId, localFilePath, cancellationToken)
                .ConfigureAwait(false);

            return AttachmentUploadResult.Success(uploaded);
        }
        catch (GoogleApiException ex) when (ex.IsMissing)
        {
            // 控えていたフォルダが無い（利用者がドライブ側で消したなど）。控えを捨てて
            // 作り直し、1回だけやり直す
            settings.Remove(FolderIdKey);

            try
            {
                var recreated = await EnsureFolderAsync(api, cancellationToken).ConfigureAwait(false);
                var uploaded = await UploadOnceAsync(api, recreated, localFilePath, cancellationToken)
                    .ConfigureAwait(false);

                return AttachmentUploadResult.Success(uploaded);
            }
            catch (GoogleApiException retry)
            {
                return AttachmentUploadResult.Failure($"アップロードできませんでした: {retry.Reason}");
            }
            catch (HttpRequestException)
            {
                return AttachmentUploadResult.Failure("通信できませんでした。オフラインの可能性があります。");
            }
        }
        catch (GoogleApiException ex)
        {
            return AttachmentUploadResult.Failure($"アップロードできませんでした: {ex.Reason}");
        }
        catch (IOException ex)
        {
            // ファイルが読めない等。壊さずに理由を出す
            return AttachmentUploadResult.Failure($"ファイルを読めませんでした: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            // オフラインなど、通信そのものが失敗した場面。落とさずに理由を出す
            return AttachmentUploadResult.Failure("通信できませんでした。オフラインの可能性があります。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が止めたのではなく、通信がタイムアウトした
            return AttachmentUploadResult.Failure("通信がタイムアウトしました。");
        }
    }

    private async Task<EventAttachment> UploadOnceAsync(
        GoogleDriveApi api, string folderId, string localFilePath, CancellationToken cancellationToken)
    {
        var mimeType = AttachmentMimeTypes.GuessFrom(localFilePath);

        var uploaded = await api
            .UploadFileAsync(folderId, localFilePath, mimeType, cancellationToken)
            .ConfigureAwait(false);

        var fileId = uploaded.GetProperty("id").GetString()
            ?? throw new GoogleApiException(System.Net.HttpStatusCode.InternalServerError, "no-id");

        var fileUrl = uploaded.TryGetProperty("webViewLink", out var link) && link.GetString() is { Length: > 0 } url
            ? url
            : $"https://drive.google.com/file/d/{fileId}/view";

        var title = uploaded.TryGetProperty("name", out var name)
            ? name.GetString()
            : Path.GetFileName(localFilePath);

        var iconLink = uploaded.TryGetProperty("iconLink", out var icon) ? icon.GetString() : null;

        return new EventAttachment(fileId, fileUrl, title, mimeType, iconLink);
    }

    private async Task<string> EnsureFolderAsync(GoogleDriveApi api, CancellationToken cancellationToken)
    {
        if (settings.Get(FolderIdKey) is { Length: > 0 } cached) return cached;

        var created = await api.CreateFolderAsync(FolderName, cancellationToken).ConfigureAwait(false);
        var id = created.GetProperty("id").GetString()
            ?? throw new GoogleApiException(System.Net.HttpStatusCode.InternalServerError, "no-id");

        settings.Set(FolderIdKey, id);
        return id;
    }
}
