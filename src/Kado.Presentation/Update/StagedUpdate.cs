using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kado.Presentation.Update;

/// <summary>
/// 落としてあって、入れ替えを待っている新しい版（自動更新の「入れ替え待ち」）。
/// <para>
/// 設定に JSON で保存する。<b>次の起動でも分かる</b>ようにするため。入れ替えた直後の起動では、
/// 同じ形で「更新しました」の知らせに使う（版・リリースのページ・変更点の本文）。
/// </para>
/// </summary>
/// <param name="Tag">タグ名（<c>v1.1.2</c>）。</param>
/// <param name="FilePath">落としたファイルの場所。</param>
/// <param name="Sha256">落としたファイルの SHA256。入れ替える直前にもう一度突き合わせる。</param>
/// <param name="ReleaseUrl">リリースのページ。</param>
/// <param name="ReleaseNotes">変更点の本文（リリース本文のまま）。</param>
public sealed record StagedUpdate(
    string Tag, string FilePath, string Sha256, string ReleaseUrl, string ReleaseNotes)
{
    /// <summary>本文の保存の上限。設定の1行に際限なく積まない。</summary>
    public const int MaxNotesLength = 20_000;

    /// <summary>タグから読んだ版。読めなければ null。</summary>
    [JsonIgnore]
    public Version? Version => ReleaseFeed.TryParseVersion(Tag, out var v) ? v : null;

    /// <summary>見つけた版と、落としたファイルから作る。ハッシュが無いときは空にする。</summary>
    public static StagedUpdate From(UpdateInfo info, string filePath)
    {
        ArgumentNullException.ThrowIfNull(info);

        var notes = info.ReleaseNotes ?? string.Empty;
        if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];

        return new StagedUpdate(info.TagName, filePath, info.Sha256 ?? string.Empty, info.ReleaseUrl ?? string.Empty, notes);
    }

    /// <summary>設定に保存する形。</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>設定から読む。空・壊れている・タグが読めないときは null。</summary>
    public static StagedUpdate? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var staged = JsonSerializer.Deserialize<StagedUpdate>(json);

            if (staged is null || staged.Tag is null || staged.FilePath is null || staged.Sha256 is null ||
                staged.ReleaseUrl is null || staged.ReleaseNotes is null)
            {
                return null;
            }

            return staged.Version is null ? null : staged;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>控えた版を、いま使ってよいかの判定の結果。</summary>
public enum StagedVerdict
{
    /// <summary>使ってよい。</summary>
    Usable,

    /// <summary>控えの版が、いまの版以下（すでに更新済み、または古い）。</summary>
    NotNewer,

    /// <summary>控えたファイルが、決められた置き場所の外にある。</summary>
    OutsideFolder,

    /// <summary>控えたファイルが無い。</summary>
    FileMissing,

    /// <summary>照合するハッシュが控えに無い。照合できないものは入れ替えない。</summary>
    ShaMissing,

    /// <summary>ファイルのハッシュが控えと合わない。</summary>
    ShaMismatch,
}

/// <summary>
/// 控えを使ってよいか。<b>落としたものをそのまま実行する</b>ので、入れ替える直前に必ずここを通す。
/// 通信も画面も持たない純粋な処理（ファイルの有無とハッシュの計算は引数で受ける）。
/// </summary>
public static class StagedUpdateCheck
{
    /// <param name="staged">控え。</param>
    /// <param name="current">いま動いている版。</param>
    /// <param name="updatesDirectory">控えを置いてよいフォルダ。</param>
    /// <param name="fileExists">ファイルがあるか。</param>
    /// <param name="sha256Of">ファイルの SHA256（16進）を求める。</param>
    public static StagedVerdict Evaluate(
        StagedUpdate staged, Version current, string updatesDirectory,
        Func<string, bool> fileExists, Func<string, string> sha256Of)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(sha256Of);

        if (staged.Version is not { } version || version <= current) return StagedVerdict.NotNewer;

        if (!IsInside(updatesDirectory, staged.FilePath)) return StagedVerdict.OutsideFolder;

        if (!fileExists(staged.FilePath)) return StagedVerdict.FileMissing;

        if (staged.Sha256.Length == 0) return StagedVerdict.ShaMissing;

        return string.Equals(sha256Of(staged.FilePath), staged.Sha256, StringComparison.OrdinalIgnoreCase)
            ? StagedVerdict.Usable
            : StagedVerdict.ShaMismatch;
    }

    /// <summary>やめた理由を、shell.log に書く言葉にする。</summary>
    public static string Describe(StagedVerdict verdict) => verdict switch
    {
        StagedVerdict.Usable => "使える",
        StagedVerdict.NotNewer => "控えの版がいまの版以下",
        StagedVerdict.OutsideFolder => "控えたファイルが決められた置き場所の外にある",
        StagedVerdict.FileMissing => "控えたファイルが無い",
        StagedVerdict.ShaMissing => "控えにSHA256が無く、照合できない",
        StagedVerdict.ShaMismatch => "ファイルのSHA256が控えと合わない",
        _ => verdict.ToString(),
    };

    private static bool IsInside(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
