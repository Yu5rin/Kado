using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kado.Data.Models;

/// <summary>
/// タスクに添えた、ファイルまたはフォルダの「場所」1件。
/// <para>
/// <b>持つのは場所（フルパス。<c>C:\...</c> や <c>\\server\share\...</c>）だけ</b>で、ファイルの中身は
/// 持たない・どこにも上げない。予定の添付（<see cref="EventAttachment"/>）が Google ドライブの
/// ファイルを指すのとは別物で、こちらはこの PC（またはその PC から見える共有）の場所を指す。
/// </para>
/// <para>
/// 場所が Windows の書き方なので、<see cref="Name"/> は <c>System.IO.Path</c> に頼らず自前で切り出す
/// （Linux で動くテストでも同じ結果にするため）。
/// </para>
/// </summary>
/// <param name="Path">フルパス。</param>
public sealed record TaskAttachment(string Path)
{
    /// <summary>一覧に出す名前。パスの最後の部分（ファイル名・フォルダ名）。切り出せなければパスのまま。</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('\\', '/');
            var separator = trimmed.LastIndexOfAny(['\\', '/']);
            var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;

            // ドライブのルート（"C:\"）は、区切りを落とすと "C:" になる。そのまま出す
            return name.Length > 0 && !name.EndsWith(':') ? name : Path;
        }
    }
}

/// <summary>
/// <see cref="TaskItem.Attachments"/>（JSON 配列の文字列）と <see cref="TaskAttachment"/> の行き来。
/// <para>
/// 形は <c>[{"path":"C:\\資料\\図面.pdf"}]</c>。要素をオブジェクトにしてあるのは、あとから項目を
/// 足しても古い版が読めるようにするため。壊れた文字列は空の一覧として読む（読めなかったからと
/// 例外にすると、そのタスクを開けなくなる）。
/// </para>
/// </summary>
public static class TaskAttachments
{
    /// <summary>1件に許す長さ。Windows のパスの上限（32767）に合わせる。</summary>
    private const int MaxPathLength = 32767;

    /// <summary>日本語のパスを <c>\uXXXX</c> に直さず、そのまま読める形で保存する。</summary>
    private static readonly JsonSerializerOptions WriteOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON から一覧を読む。無い・壊れている・空なら空の一覧。</summary>
    public static IReadOnlyList<TaskAttachment> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return [];

            var result = new List<TaskAttachment>();

            foreach (var item in array)
            {
                if (item is not JsonObject obj) continue;
                if (obj["path"] is not JsonValue value || !value.TryGetValue<string>(out var path)) continue;
                if (string.IsNullOrWhiteSpace(path)) continue;

                // 同じ場所が二度入っていても、一覧には一度だけ出す
                if (result.Any(a => SamePlace(a.Path, path))) continue;

                result.Add(new TaskAttachment(path));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 一覧を JSON にする。<b>空なら null</b>（列に空の配列を残さない。「持っていない」は NULL で表す）。
    /// </summary>
    public static string? ToJson(IEnumerable<TaskAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var array = new JsonArray();
        foreach (var attachment in attachments) array.Add(new JsonObject { ["path"] = attachment.Path });

        return array.Count == 0 ? null : array.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// 同じ場所か。Windows のパスは大文字小文字を区別せず、末尾の区切りの有無も気にしない
    /// （<c>C:\資料</c> と <c>c:\資料\</c> は同じフォルダ）。
    /// </summary>
    public static bool SamePlace(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 場所を足した一覧を返す。<b>同じ場所はもう一度足さない</b>。空の文字列・長すぎる文字列は無視する。
    /// </summary>
    /// <param name="current">いまの一覧。</param>
    /// <param name="paths">足したい場所。</param>
    public static IReadOnlyList<TaskAttachment> Add(IReadOnlyList<TaskAttachment> current, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(paths);

        var result = current.ToList();

        foreach (var raw in paths)
        {
            var path = raw?.Trim();
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength) continue;
            if (result.Any(a => SamePlace(a.Path, path))) continue;

            result.Add(new TaskAttachment(path));
        }

        return result;
    }

    /// <summary>一覧から場所を外す。</summary>
    public static IReadOnlyList<TaskAttachment> Remove(IReadOnlyList<TaskAttachment> current, string path)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(path);

        return current.Where(a => !SamePlace(a.Path, path)).ToList();
    }

    /// <summary>比べるための形。前後の空白と、末尾の区切り（ルートを除く）を落とす。</summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Trim();

        // "C:\" のようなルートは区切りを落とすと別物（"C:" はカレントディレクトリ）になるので残す
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }
}
