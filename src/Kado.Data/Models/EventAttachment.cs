namespace Kado.Data.Models;

/// <summary>
/// 予定の添付1件。Google Calendar の <c>attachments[]</c> の1要素にあたる。
/// <para>
/// 添付そのもの（ファイルの中身）は持たない。Google ドライブに上がっているファイルへの
/// 参照（<see cref="FileUrl"/>）だけを予定に添える。ファイルの共有設定はこちらでは触らない。
/// </para>
/// </summary>
/// <param name="FileId">ドライブ側のファイル ID。</param>
/// <param name="FileUrl">開くための URL。<b>https のものしか開かない</b>（安全のため）。</param>
/// <param name="Title">一覧に出す題名。</param>
/// <param name="MimeType">種類が分かる程度の表示に使う。</param>
/// <param name="IconLink">ドライブが返すアイコン画像の URL。無ければ null。</param>
public sealed record EventAttachment(
    string FileId,
    string FileUrl,
    string? Title,
    string? MimeType,
    string? IconLink = null);
