namespace Kado.Presentation.Sync;

/// <summary>
/// 拡張子からおおまかな種類を当てる。
/// <para>
/// アップロード時に添える <c>mimeType</c> と、一覧に出す「種類が分かる程度の表示」に使う。
/// 厳密さは要らない（ドライブ側は中身も見て決める）ので、よくある拡張子だけ持つ。
/// </para>
/// </summary>
internal static class AttachmentMimeTypes
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".zip"] = "application/zip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
    };

    /// <summary>拡張子から当てる。分からなければ既定の種類。</summary>
    public static string GuessFrom(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out var mime) ? mime : "application/octet-stream";
}
