using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Google.Mapping;

/// <summary>
/// Google Tasks のタスクと、こちらの <see cref="TaskItem"/> を行き来する。
/// <para>
/// <b>期限に時刻は持てない。</b>Google は <c>due</c> を RFC3339 で持つが、時刻の部分は
/// 捨てられる（API の仕様として「現在は時刻を記録できない」と明記されている）。
/// こちらも日付だけなので都合はよいが、<c>2026-09-24T00:00:00.000Z</c> という形で
/// 送り返す必要がある。日付だけの文字列は受け取ってもらえない。
/// </para>
/// <para>
/// <b>UTC で解釈される点に注意。</b>日本時間で期限を組み立ててから UTC へ直すと、
/// 9時間ぶん前の日付になって1日ずれる。日付はそのまま UTC の 00:00 として送る。
/// </para>
/// <para>
/// 親子関係と並び順は <c>move</c> でしか変えられず、本文の書き戻しでは動かない。
/// 受け取った値を控えるだけにして、こちらからは動かさない。
/// </para>
/// </summary>
public static class TaskMapper
{
    /// <summary>Google が消したタスクに立てる印。</summary>
    public const string DeletedFlag = "deleted";

    /// <summary>完了を表す状態。</summary>
    public const string CompletedStatus = "completed";

    /// <summary>未完了を表す状態。</summary>
    public const string NeedsActionStatus = "needsAction";

    /// <summary>取り込み元の呼び名。</summary>
    public const string SourceName = "google";

    /// <summary>
    /// Google のタスクを読む。
    /// <para><paramref name="existing"/> があれば、ローカルにしか無い項目を引き継ぐ。</para>
    /// </summary>
    /// <param name="element">タスク1件の JSON。</param>
    /// <param name="taskListId">取りに行ったリストの ID。</param>
    /// <param name="localListId">こちらのタスクリスト ID。Google のリストと対応付けたもの。</param>
    /// <param name="existing">すでに持っている同じタスク。初回なら null。</param>
    /// <param name="now">ローカルの更新時刻に入れる値。</param>
    public static TaskItem FromGoogle(
        JsonElement element,
        string taskListId,
        string? localListId = null,
        TaskItem? existing = null,
        DateTimeOffset? now = null)
    {
        var googleId = element.Text("id")
            ?? throw new ArgumentException("id の無いタスクは読めません。", nameof(element));

        var isDone = string.Equals(element.Text("status"), CompletedStatus, StringComparison.OrdinalIgnoreCase);

        return new TaskItem
        {
            Id = existing?.Id ?? $"google:{googleId}",

            Title = element.Text("title") ?? string.Empty,
            Due = ReadDue(element.Text("due")),
            IsDone = isDone,
            Note = element.Text("notes"),

            // 所属はこちらの ID を保つ。Google のリスト ID をそのまま出すと画面に出てしまう
            TaskListId = localListId ?? existing?.TaskListId,

            // 完了したことだけでは、どちらが新しいか判定できない
            CompletedAt = isDone
                ? ParseMoment(element.Text("completed")) ?? existing?.CompletedAt ?? now ?? DateTimeOffset.Now
                : null,

            // 親子と並び順は move でしか変えられない。受け取った値を控えるだけ
            ParentId = element.Text("parent"),
            Position = element.Text("position"),

            GoogleRaw = GoogleJson.Normalize(element),
            GoogleTaskId = googleId,
            GoogleTaskListId = taskListId,
            GoogleUpdated = element.Text("updated"),
            Source = SourceName,
            UpdatedAt = now ?? DateTimeOffset.Now,

            // 作成日時・並び順はローカルにしか無い項目。既存があれば引き継ぐ。
            // 初めて受け取ったタスクは、こちらが受け取った時刻を作成日時にする
            // （Google Tasks の作成日時は API で取れないため）
            CreatedAt = existing?.CreatedAt ?? now ?? DateTimeOffset.Now,
            SortOrder = existing?.SortOrder ?? 0,
        };
    }

    /// <summary>
    /// このタスクは消されたか。
    /// <para>
    /// Google Tasks は予定と違って <c>deleted: true</c> の印を立てる。
    /// <c>hidden</c>（完了して一覧から隠れただけ）とは別物なので、混ぜない。
    /// </para>
    /// </summary>
    public static bool IsDeleted(JsonElement element) => element.Flag(DeletedFlag);

    /// <summary>
    /// 書き戻す本文を作る。
    /// <para>親子関係と並び順は入れない。本文では動かせず、送っても無視されるため。</para>
    /// </summary>
    public static JsonObject ToGoogle(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new JsonObject
        {
            ["title"] = value.Title,
            ["notes"] = value.Note,

            // null を送ると期限を外す意味になる。「いつやるか未定」はそれで正しい
            ["due"] = value.Due is { } due ? FormatDue(due) : null,
            ["status"] = value.IsDone ? CompletedStatus : NeedsActionStatus,
        };
    }

    /// <summary>書き戻す必要があるか。控えた生データと見比べる。</summary>
    public static bool NeedsPush(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.GoogleRaw is not { Length: > 0 } raw) return true;

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject original) return true;

            var wanted = ToGoogle(value);
            foreach (var pair in wanted)
            {
                // 期限は書き方の揺れ（小数秒の桁など）があるので、日付に直して比べる
                if (string.Equals(pair.Key, "due", StringComparison.Ordinal))
                {
                    if (ReadDue(original["due"]?.GetValue<string>()) != value.Due) return true;
                    continue;
                }

                if (!GoogleJson.SameContent(
                        pair.Value?.ToJsonString(), original[pair.Key]?.ToJsonString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            // due が文字列でなかった。取り直せば直る
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 期限
    // ------------------------------------------------------------------

    /// <summary>
    /// 期限を読む。
    /// <para>
    /// <c>2026-09-24T00:00:00.000Z</c> で届く。時差で直すと前日になるので、
    /// <b>UTC のまま日付だけ</b>を取る。
    /// </para>
    /// </summary>
    private static DateOnly? ReadDue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }

        // 日付だけで来ることもある
        return DateOnly.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// 期限を書く。
    /// <para>
    /// UTC の 00:00 として送る。日本時間の 00:00 を UTC に直すと前日の15時になり、
    /// 日付が1日ずれる。
    /// </para>
    /// </summary>
    private static string FormatDue(DateOnly due) =>
        due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000Z";

    private static DateTimeOffset? ParseMoment(string? text) =>
        text is not null && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
