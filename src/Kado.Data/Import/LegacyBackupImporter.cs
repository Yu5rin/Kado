using System.Globalization;
using System.Text.Json;
using Kado.Core.Recurrence;
using Kado.Data.Models;

namespace Kado.Data.Import;

/// <summary>
/// 旧 Edge 拡張 inaCalendar のバックアップ JSON を読み込む。
/// <para>
/// 形式の詳細は <c>tests/Kado.Data.Tests/TestData/README.md</c> を参照。
/// </para>
/// <para>
/// <b>予定に付いていた ToDo フラグはタスクへ変換する</b>（要件書 8 章）。データモデルを
/// 分離したことによる唯一の変換処理なので、変換ログを残して結果を確認できるようにする。
/// </para>
/// <para>
/// 読めない行があっても止めない。1件のために移行全体が失敗すると、どこまで移せたのか
/// 分からなくなる。落とした行はログに残して先へ進む。
/// </para>
/// </summary>
public sealed class LegacyBackupImporter
{
    /// <summary>旧データから取り込んだ予定に付ける印。</summary>
    public const string ImportedSource = "legacy-import";

    /// <summary>ToDo フラグから変換したタスクに付ける印。あとから追跡できるようにする。</summary>
    public const string ConvertedTaskSource = "legacy-todo";

    private readonly List<ImportLogEntry> _log = [];

    /// <summary>バックアップ JSON を読み込む。</summary>
    /// <exception cref="InvalidDataException">JSON として読めない、または形式が想定と違う。</exception>
    public LegacyImportResult Import(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        _log.Clear();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"バックアップを JSON として読めませんでした: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("バックアップの最上位がオブジェクトではありません。");
            }

            var app = GetString(root, "app") ?? "(不明)";
            var version = GetInt(root, "version") ?? 0;
            var exportedAt = ParseTimestamp(GetString(root, "exportedAt"));

            if (version is not 3)
            {
                Warn("形式", $"想定しているのは形式 v3 ですが v{version} でした。読めるところまで取り込みます。");
            }

            var events = new List<CalendarEvent>();
            var tasks = new List<TaskItem>();

            ReadUserEvents(root, events, tasks);
            ReadRecurringEvents(root, events);

            var (days, rangeStart, rangeEnd) = ReadWorkingDays(root);
            var settings = ReadSettings(root);

            Info("集計", $"予定 {events.Count} 件、タスク {tasks.Count} 件を取り込みました。");

            return new LegacyImportResult(
                app, version, exportedAt,
                events, tasks, days, rangeStart, rangeEnd, settings,
                _log.ToArray());
        }
    }

    // ------------------------------------------------------------------
    // 予定とタスク
    // ------------------------------------------------------------------

    private void ReadUserEvents(JsonElement root, List<CalendarEvent> events, List<TaskItem> tasks)
    {
        if (!root.TryGetProperty("userEvents", out var userEvents)
            || userEvents.ValueKind != JsonValueKind.Object)
        {
            Warn("予定", "userEvents がありません。予定は取り込めませんでした。");
            return;
        }

        var converted = 0;

        foreach (var day in userEvents.EnumerateObject())
        {
            var date = ParseDate(day.Name);
            if (date is null)
            {
                Error("予定", $"日付として読めないキーがありました: '{day.Name}'。この日の予定を飛ばしました。");
                continue;
            }

            if (day.Value.ValueKind != JsonValueKind.Array)
            {
                Error("予定", $"{day.Name} の値が配列ではありません。飛ばしました。");
                continue;
            }

            foreach (var item in day.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Error("予定", $"{day.Name} に予定として読めない要素がありました。飛ばしました。");
                    continue;
                }

                // ToDo フラグの付いた予定はタスクへ移す。
                // 予定として残すと「期限だけある」状態を表現できない（要件書 3.1）
                if (GetBool(item, "todo") == true)
                {
                    tasks.Add(ToTask(item, date.Value));
                    converted++;
                    continue;
                }

                events.Add(ToEvent(item, date.Value));
            }
        }

        if (converted > 0)
        {
            Info("タスク変換", $"ToDo フラグの付いた予定 {converted} 件をタスクへ変換しました。");
        }
    }

    private CalendarEvent ToEvent(JsonElement item, DateOnly date)
    {
        var id = GetString(item, "uid") ?? NewId();
        var start = ParseTime(GetString(item, "start"));
        var end = ParseTime(GetString(item, "end"));

        // 時刻は開始と終了がそろっている前提だが、片方だけの行が混じっても落とさない
        if (start is not null && end is null)
        {
            Warn("予定", "終了時刻がないため終日として扱いました。", id);
            start = null;
        }

        var endDate = ParseDate(GetString(item, "endDate"));
        if (endDate is { } e && e < date)
        {
            Warn("予定", $"終了日 {e:yyyy/MM/dd} が開始日より前でした。終了日を無視します。", id);
            endDate = null;
        }

        return new CalendarEvent
        {
            Id = id,
            Title = GetString(item, "text") ?? "(無題)",
            Date = date,
            EndDate = endDate,
            StartTime = start,
            EndTime = start is null ? null : end,
            Location = GetString(item, "location"),
            Note = GetString(item, "note"),
            Color = GetString(item, "color"),
            CalendarId = GetString(item, "calId"),
            GoogleEventId = GetString(item, "gcalId"),
            GoogleUpdated = GetString(item, "gUpdated"),
            Source = GetString(item, "src") ?? ImportedSource,
            UpdatedAt = ParseEpochMilliseconds(item, "updatedAt"),
        };
    }

    private TaskItem ToTask(JsonElement item, DateOnly date)
    {
        var id = GetString(item, "uid") ?? NewId();
        var title = GetString(item, "text") ?? "(無題)";

        // done が無い＝まだ完了していない。旧データでは未完了のとき項目ごと省かれている
        var done = GetBool(item, "done") ?? false;

        var start = GetString(item, "start");
        if (start is not null)
        {
            // タスクに開始時刻は無いので持って来られない。仮に持てたとしても、
            // 旧データの時刻が「作業予定」なのか「予定の時刻」なのか判別できないため捨てる
            Warn("タスク変換", $"開始時刻 {start} は引き継げないため落としました。", id);
        }

        Info("タスク変換", $"予定「{title}」をタスクへ変換しました（期限 {date:yyyy/MM/dd}、"
                        + $"{(done ? "完了済み" : "未完了")}）。", id);

        return new TaskItem
        {
            Id = id,
            Title = title,
            Due = date,
            IsDone = done,
            Note = GetString(item, "note"),
            TaskListId = GetString(item, "gtaskListId"),
            GoogleTaskId = GetString(item, "gtaskId"),
            GoogleTaskListId = GetString(item, "gtaskListId"),
            GoogleUpdated = GetString(item, "gtaskUpdated"),
            Source = ConvertedTaskSource,
            UpdatedAt = ParseEpochMilliseconds(item, "updatedAt"),
        };
    }

    // ------------------------------------------------------------------
    // 繰り返し予定
    // ------------------------------------------------------------------

    private void ReadRecurringEvents(JsonElement root, List<CalendarEvent> events)
    {
        if (!root.TryGetProperty("recurringEvents", out var recurring)
            || recurring.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in recurring.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = GetString(item, "uid") ?? NewId();
            var from = ParseDate(GetString(item, "from"));

            if (from is null)
            {
                Error("繰り返し", "開始日（from）が読めないため取り込めませんでした。", id);
                continue;
            }

            var spec = BuildRecurrenceSpec(item, id);
            var start = ParseTime(GetString(item, "start"));
            var end = ParseTime(GetString(item, "end"));

            events.Add(new CalendarEvent
            {
                Id = id,
                Title = GetString(item, "text") ?? "(無題)",
                Date = from.Value,
                StartTime = start,
                EndTime = start is null ? null : end,
                Location = GetString(item, "location"),
                Note = GetString(item, "note"),
                Color = GetString(item, "color"),
                CalendarId = GetString(item, "calId"),
                Recurrence = spec,
                GoogleEventId = GetString(item, "gcalId"),
                GoogleUpdated = GetString(item, "gUpdated"),
                Source = ImportedSource,
                UpdatedAt = ParseEpochMilliseconds(item, "updatedAt"),
            });
        }
    }

    /// <summary>
    /// 旧形式の <c>rule</c> を RRULE サブセットに直す。
    /// <para>
    /// 実データで確認できたのは <c>yearly</c> だけなので、他の種別は形を推測している。
    /// 推測が外れても繰り返しが消えるだけで済むよう、<b>日付を特定する情報が無いときは
    /// 指定を省いて開始日から導かせる</b>（RecurrenceRule 側の既定動作）。
    /// </para>
    /// </summary>
    private string? BuildRecurrenceSpec(JsonElement item, string id)
    {
        if (!item.TryGetProperty("rule", out var rule) || rule.ValueKind != JsonValueKind.Object)
        {
            Warn("繰り返し", "rule が無いため単発の予定として取り込みました。", id);
            return null;
        }

        var type = GetString(rule, "type")?.ToLowerInvariant();
        var parts = new List<string>();

        switch (type)
        {
            case "daily":
                parts.Add("FREQ=DAILY");
                break;

            case "weekly":
                parts.Add("FREQ=WEEKLY");
                if (ReadWeekDays(rule) is { Count: > 0 } days)
                {
                    parts.Add("BYDAY=" + string.Join(",", days));
                }
                else
                {
                    Warn("繰り返し", "曜日の指定が読めないため、開始日と同じ曜日として扱います。", id);
                }
                break;

            case "monthly":
                parts.Add("FREQ=MONTHLY");
                if (GetInt(rule, "day") is { } monthDay) parts.Add($"BYMONTHDAY={monthDay}");
                else Warn("繰り返し", "日の指定が読めないため、開始日と同じ日として扱います。", id);
                break;

            case "yearly":
                parts.Add("FREQ=YEARLY");
                if (GetInt(rule, "month") is { } month) parts.Add($"BYMONTH={month}");
                if (GetInt(rule, "day") is { } yearDay) parts.Add($"BYMONTHDAY={yearDay}");
                break;

            default:
                Error("繰り返し", $"未知の繰り返し種別 '{type ?? "(なし)"}' のため、"
                               + "単発の予定として取り込みました。繰り返しは失われています。", id);
                return null;
        }

        if (GetInt(rule, "interval") is { } interval && interval > 1) parts.Add($"INTERVAL={interval}");
        if (ParseDate(GetString(item, "until")) is { } until) parts.Add($"UNTIL={until:yyyyMMdd}");

        var except = ReadExceptDates(item, id);
        if (except.Count > 0) parts.Add("EXDATE=" + string.Join(",", except));

        var spec = string.Join(";", parts);

        // 組み立てた指定が Core で読めることを確かめる。読めないものを保存すると
        // 表示のたびに失敗するので、ここで単発に倒しておく
        if (!RecurrenceRule.TryParse(spec, out _))
        {
            Error("繰り返し", $"組み立てた繰り返し指定 '{spec}' を解釈できませんでした。"
                           + "単発の予定として取り込みました。", id);
            return null;
        }

        return spec;
    }

    private static IReadOnlyList<string> ReadWeekDays(JsonElement rule)
    {
        // 日曜を 0 とする番号の配列を想定する。キー名は実データで確認できていないため
        // 考えられるものを順に探す
        foreach (var key in (string[])["days", "weekdays", "dow"])
        {
            if (!rule.TryGetProperty(key, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                var codes = value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.Number)
                    .Select(v => v.GetInt32())
                    .Where(v => v is >= 0 and <= 6)
                    .Select(ToDayCode)
                    .ToArray();
                if (codes.Length > 0) return codes;
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                var v = value.GetInt32();
                if (v is >= 0 and <= 6) return [ToDayCode(v)];
            }
        }

        return [];
    }

    private static string ToDayCode(int sundayBased) =>
        (string[])["SU", "MO", "TU", "WE", "TH", "FR", "SA"] is var codes ? codes[sundayBased] : "SU";

    private List<string> ReadExceptDates(JsonElement item, string id)
    {
        var result = new List<string>();

        if (!item.TryGetProperty("except", out var except) || except.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var value in except.EnumerateArray())
        {
            var date = ParseDate(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            if (date is null)
            {
                Warn("繰り返し", $"除外日として読めない値がありました: '{value}'。飛ばしました。", id);
                continue;
            }
            result.Add(date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 実働日と設定
    // ------------------------------------------------------------------

    private (IReadOnlyList<DateOnly> Days, DateOnly? Start, DateOnly? End) ReadWorkingDays(JsonElement root)
    {
        var days = new List<DateOnly>();

        if (root.TryGetProperty("workingDays", out var workingDays)
            && workingDays.ValueKind == JsonValueKind.Array)
        {
            var skipped = 0;
            foreach (var value in workingDays.EnumerateArray())
            {
                var date = ParseDate(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
                if (date is null) { skipped++; continue; }
                days.Add(date.Value);
            }

            if (skipped > 0) Warn("実働日", $"日付として読めない値を {skipped} 件飛ばしました。");
            Info("実働日", $"稼働日を {days.Count} 件取り込みました。");
        }
        else
        {
            Warn("実働日", "workingDays がありません。実働日データは取り込めませんでした。");
        }

        // 範囲は明示されていればそれに従う。無ければ日付の最小・最大から決める
        var start = ParseDate(GetString(root, "dataStart")) ?? (days.Count > 0 ? days.Min() : null);
        var end = ParseDate(GetString(root, "dataEnd")) ?? (days.Count > 0 ? days.Max() : null);

        Info("実働日", "マイルストーンはバックアップに含まれないため、"
                   + "実働日 Excel から取り込み直す必要があります。");

        return (days, start, end);
    }

    private Dictionary<string, string> ReadSettings(JsonElement root)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!root.TryGetProperty("settings", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            Warn("設定", "settings がありません。既定値で始まります。");
            return settings;
        }

        foreach (var property in element.EnumerateObject())
        {
            // 構造のある値は JSON のまま保存する。意味づけは使う側で行う
            settings[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                _ => property.Value.GetRawText(),
            };
        }

        Info("設定", $"{settings.Count} 項目を取り込みました。");

        // 現行のビュー別表示項目 ON/OFF は廃止する（要件書 5.5）。値は残すが使わない
        if (settings.ContainsKey("show") || settings.ContainsKey("fontSize"))
        {
            Warn("設定", "ビュー別の表示項目 ON/OFF（show・fontSize）は新しい UI では使いません。"
                      + "値は残してありますが、表示はレイアウト側で解決します。");
        }

        Warn("同期状態", "同期トークンがバックアップに含まれていないため、"
                     + "初回は全再同期が必要です。予定の Google ID は引き継がれるので再リンクはできます。");

        return settings;
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private void Info(string category, string message, string? id = null) =>
        _log.Add(new ImportLogEntry(ImportLogLevel.Info, category, message, id));

    private void Warn(string category, string message, string? id = null) =>
        _log.Add(new ImportLogEntry(ImportLogLevel.Warning, category, message, id));

    private void Error(string category, string message, string? id = null) =>
        _log.Add(new ImportLogEntry(ImportLogLevel.Error, category, message, id));

    private static string NewId() => Guid.NewGuid().ToString("N")[..15];

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : null;

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : null;

    private static DateTimeOffset? ParseTimestamp(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : null;

    private static DateTimeOffset ParseEpochMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UtcNow;
}
