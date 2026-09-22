using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Core.WorkingDays;

namespace Kado.Core.Import;

/// <summary>
/// 配信用の実働日データ（feed.json）の読み書き。
/// <para>
/// 旧 inaCalendar と同じ形。配布の Excel を全員に配る代わりに、1か所に置いた
/// ファイルを各端末が取りに行くための入れ物。
/// </para>
/// <code>
/// {
///   "app": "inaCalendar", "type": "workingdays", "updatedAt": "2026-09-20",
///   "workingDays": ["2026-09-01", …], "dataStart": "…", "dataEnd": "…",
///   "milestones": [{ "date": "2026-09-14", "name": "仕様期限" }]
/// }
/// </code>
/// <para>
/// マイルストーンは旧の書き出しには入っていないが、読めるようにしてある。
/// 無ければ稼働日だけを取り込む。
/// </para>
/// </summary>
public static class WorkdayFeed
{
    /// <summary>この形式だと名乗る文字。</summary>
    public const string Kind = "workingdays";

    /// <summary>
    /// マイルストーンの名前の長さの上限。
    /// <para>これを超える名前は、その要素ごと読み飛ばす。8MB のファイルなら任意の長さの
    /// 名前が入りうるが、DB にも画面にもそのまま流れるので上限を設けている。</para>
    /// </summary>
    private const int MaxNameLength = 256;

    /// <summary>
    /// 稼働日・マイルストーンそれぞれの件数の上限。
    /// <para>これを超えたぶんは読み飛ばす。何件読み飛ばしたかは <see cref="ImportResult.Warnings"/>
    /// に足す。</para>
    /// </summary>
    private const int MaxItems = 10_000;

    /// <summary>読み込む。形として成り立っていなければ例外。</summary>
    /// <param name="json">feed.json の中身。</param>
    public static ImportResult Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"実働日データとして読めませんでした: {ex.Message}", ex);
        }

        if (root is not JsonObject body) throw new InvalidDataException("実働日データの形ではありません。");

        var warnings = new List<string>();

        if (Text(body, "type") is { Length: > 0 } type && !string.Equals(type, Kind, StringComparison.Ordinal))
        {
            warnings.Add($"種類が「{type}」になっています。実働日データとして読み進めます。");
        }

        var days = new List<DateOnly>();
        var droppedDaysForCount = 0;
        if (body["workingDays"] is JsonArray list)
        {
            foreach (var item in list)
            {
                // 要素が数値などだと GetValue<string>() は例外を投げる。1件おかしいだけで
                // 全体を止めないよう、型が違うものは値を読まずに読み飛ばす
                var text = AsString(item);

                if (text is null || ParseDate(text) is not { } date)
                {
                    warnings.Add($"日付として読めない値がありました: {item}");
                    continue;
                }

                if (days.Count >= MaxItems)
                {
                    droppedDaysForCount++;
                    continue;
                }

                days.Add(date);
            }
        }

        days = days.Distinct().Order().ToList();
        if (days.Count == 0) throw new InvalidDataException("稼働日が1件も入っていません。");

        if (droppedDaysForCount > 0)
        {
            warnings.Add($"稼働日が上限（{MaxItems}件）を超えたため、{droppedDaysForCount}件を読み飛ばしました。");
        }

        var milestones = new List<Milestone>();
        var droppedMilestonesForCount = 0;
        var droppedMilestonesForNameLength = 0;
        if (body["milestones"] is JsonArray marks)
        {
            foreach (var item in marks)
            {
                if (item is not JsonObject mark)
                {
                    warnings.Add($"マイルストーンとして読めない要素がありました: {item}");
                    continue;
                }

                var date = ParseDate(Text(mark, "date"));
                var name = Text(mark, "name");

                if (date is not { } at || name is not { Length: > 0 })
                {
                    warnings.Add($"マイルストーンとして読めない要素がありました: {item}");
                    continue;
                }

                if (name.Length > MaxNameLength)
                {
                    droppedMilestonesForNameLength++;
                    continue;
                }

                if (milestones.Count >= MaxItems)
                {
                    droppedMilestonesForCount++;
                    continue;
                }

                milestones.Add(new Milestone(at, name, Text(body, "updatedAt")));
            }
        }

        milestones = milestones.OrderBy(m => m.Date).ToList();

        if (droppedMilestonesForNameLength > 0)
        {
            warnings.Add(
                $"名前が長すぎる（{MaxNameLength}文字超）マイルストーンを{droppedMilestonesForNameLength}件読み飛ばしました。");
        }

        if (droppedMilestonesForCount > 0)
        {
            warnings.Add($"マイルストーンが上限（{MaxItems}件）を超えたため、{droppedMilestonesForCount}件を読み飛ばしました。");
        }

        return new ImportResult(
            Text(body, "updatedAt") ?? string.Empty,
            ParseDate(Text(body, "dataStart")) ?? days[0],
            ParseDate(Text(body, "dataEnd")) ?? days[^1],
            milestones.Count > 0 ? milestones[0].Date : null,
            milestones.Count > 0 ? milestones[^1].Date : null,
            days,
            milestones,
            warnings);
    }

    /// <summary>いまの実働日データを配信用に書き出す。</summary>
    /// <param name="calendar">書き出すカレンダー。</param>
    /// <param name="updatedAt">書き出した日。</param>
    public static string Write(WorkingDayCalendar calendar, DateOnly updatedAt)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var days = calendar.Days.Order().ToArray();
        if (days.Length == 0) throw new InvalidOperationException("書き出せる実働日データがありません。");

        var body = new JsonObject
        {
            ["app"] = "inaCalendar",
            ["type"] = Kind,
            ["updatedAt"] = Format(updatedAt),
            ["workingDays"] = new JsonArray(days.Select(d => (JsonNode)Format(d)!).ToArray()),
            ["dataStart"] = Format(calendar.RangeStart ?? days[0]),
            ["dataEnd"] = Format(calendar.RangeEnd ?? days[^1]),
        };

        if (calendar.AllMilestones.Count > 0)
        {
            body["milestones"] = new JsonArray(calendar.AllMilestones
                .OrderBy(m => m.Date)
                .Select(m => (JsonNode)new JsonObject
                {
                    ["date"] = Format(m.Date),
                    ["name"] = m.Name,
                })
                .ToArray());
        }

        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// 配列の要素を文字列として読む。
    /// <para>数値など文字列以外の要素だと <c>null</c>。<c>JsonNode.GetValue&lt;string&gt;()</c>
    /// は型が違うと例外を投げるので、それを避けるために <c>TryGetValue</c> を使う。</para>
    /// </summary>
    private static string? AsString(JsonNode? item) =>
        item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
