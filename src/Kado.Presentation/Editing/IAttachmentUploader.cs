using Kado.Data.Models;

namespace Kado.Presentation.Editing;

/// <summary>
/// 添付を上げる口。
/// <para>
/// ViewModel から直接 Google ドライブへ触ると WPF なしでは試験できなくなる。
/// <see cref="IFileDialogs"/> と同じ考え方で、出す側（実物は Google ドライブへの
/// アップロード）を差し替えられるようにする。
/// </para>
/// </summary>
public interface IAttachmentUploader
{
    /// <summary>
    /// ファイルを1つ上げて、予定の添付として使える形にする。
    /// <para>
    /// 権限（<c>drive.file</c>）が無ければ、実物の実装は追加の同意を先に求める。
    /// オフラインなど、上げられない事情はすべて <see cref="AttachmentUploadResult.ErrorMessage"/>
    /// に理由を入れて返す（例外を投げない）。
    /// </para>
    /// </summary>
    Task<AttachmentUploadResult> UploadAsync(string localFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// アップロードの結果。
/// <para><see cref="Attachment"/> があれば成功。無ければ <see cref="ErrorMessage"/> に理由が入る。</para>
/// </summary>
public sealed record AttachmentUploadResult(EventAttachment? Attachment, string? ErrorMessage)
{
    public bool Succeeded => Attachment is not null;

    public static AttachmentUploadResult Success(EventAttachment attachment) => new(attachment, null);

    public static AttachmentUploadResult Failure(string message) => new(null, message);
}

/// <summary>何もできない実装。Google に繋いでいない画面や試験で使う。</summary>
public sealed class NullAttachmentUploader : IAttachmentUploader
{
    public static readonly NullAttachmentUploader Instance = new();

    private NullAttachmentUploader() { }

    public Task<AttachmentUploadResult> UploadAsync(
        string localFilePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(AttachmentUploadResult.Failure("Google に接続していません。"));
}
