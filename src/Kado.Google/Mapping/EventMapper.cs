using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Data.Models;

namespace Kado.Google.Mapping;

/// <summary>
/// Google Calendar のイベントと、こちらの <see cref="CalendarEvent"/> を行き来する。
/// <para>
/// <b>終日予定の終了日は、Google では「翌日」を指す。</b>9月24日だけの終日予定は
/// <c>start.date=2026-09-24, end.date=2026-09-25</c> で届く（排他）。こちらの
/// <see cref="CalendarEvent.EndDate"/> はその日を含む（包含）ので、読むときは1日引き、
/// 書くときは1日足す。ここを取り違えると、同期のたびに予定が1日ずつ伸びていく。
/// </para>
/// <para>
/// 書き戻しは <c>patch</c> を使い、<b>こちらが扱う項目だけ</b>を送る。ゲストや通知、
/// 会議室の予約など、このアプリに欄が無いものは送らない。送らなければ Google 側は
/// 無傷で残る（要件書 6.3）。とくに配列は丸ごと置き換わるので、<c>attendees</c> を
/// うっかり空で送るとゲストが全員消える。
/// </para>
/// </summary>
public static class EventMapper
{
    /// <summary>Google が取り消した予定に付ける状態。</summary>
    public const string CancelledStatus = "cancelled";

    /// <summary>取り込み元の呼び名。</summary>
    public const string SourceName = "google";

    /// <summary>
    /// Google のイベントを読む。
    /// <para>
    /// <paramref name="existing"/> があれば、ローカルにしか無い項目（ID、所属カレンダー、
    /// 作業時間など）を引き継ぐ。無ければ新しく作る。
    /// </para>
    /// </summary>
    /// <param name="element">イベント1件の JSON。</param>
    /// <param name="calendarId">
    /// このイベントを<b>こちらのどのカレンダーに入れるか</b>（<see cref="CalendarEvent.CalendarId"/>
    /// に入る値）。呼び出し側（<see cref="Sync.EventSyncEngine"/>）ではローカルのカレンダー ID を渡す。
    /// </param>
    /// <param name="existing">すでに持っている同じ予定。初回なら null。</param>
    /// <param name="now">ローカルの更新時刻に入れる値。</param>
    /// <param name="googleCalendarId">
    /// Google 側で<b>実際にこのイベントが入っているカレンダーの ID</b>
    /// （<see cref="CalendarEvent.GoogleCalendarId"/> に入る値）。省略すると
    /// <paramref name="calendarId"/> と同じものとして扱う（同期対象のカレンダーを
    /// 自分自身の ID として渡す、ふつうの呼び方に合わせた既定値）。
    /// </param>
    public static CalendarEvent FromGoogle(
        JsonElement element,
        string calendarId,
        CalendarEvent? existing = null,
        DateTimeOffset? now = null,
        string? googleCalendarId = null)
    {
        var googleId = element.Text("id")
            ?? throw new ArgumentException("id の無いイベントは読めません。", nameof(element));

        var start = element.Child("start");
        var end = element.Child("end");

        var (date, startTime) = ReadStart(start);
        var (endDate, endTime) = ReadEnd(end, date, startTime is not null);

        // 今回、実際に取りに行った Google 側のカレンダー
        var actualGoogleCalendarId = googleCalendarId ?? calendarId;

        return new CalendarEvent
        {
            // ローカルの識別子は変えない。変えると作業時間や Undo の参照が切れる
            Id = existing?.Id ?? $"google:{googleId}",

            Title = element.Text("summary") ?? string.Empty,
            Date = date,
            EndDate = endDate,
            StartTime = startTime,
            EndTime = endTime,
            Location = element.Text("location"),
            Note = element.Text("description"),

            Url = element.Child("source")?.Text("url"),
            SourceTitle = element.Child("source")?.Text("title"),

            // 色はカレンダーで決まる。イベント個別の colorId は取り込まない
            Color = existing?.Color,

            // 通知の指定はこちら独自の項目で、Google には対応する欄が無い。引き継がないと、
            // PushChangesAsync が PATCH の応答をそのまま FromGoogle に通して上書き保存する
            // たびに、この予定だけの通知指定が消える
            Notify = existing?.Notify,

            // こちらの希望（入れ先）は編集画面で決める。取りに行ったカレンダーをそのまま
            // 入れ先にするのが既定。ただし「こちらで移す指示（CalendarId の変更）がまだ
            // 送れていない」ときだけは例外で、既存の希望を保つ。
            //
            // 判定は、Google 側で最後に確かめた場所（existing.GoogleCalendarId）が
            // 今回取りに行ったカレンダーと同じ（＝ Google はまだ動いていない）で、かつ
            // こちらの希望が今回のカレンダーと違う（＝移す指示がある）とき。
            //
            // ここを「既存があれば常に希望を保つ」にすると、Google 側（Web など）で
            // 実際に別のカレンダーへ移されたときに追従できず、しかも移動元からの
            // cancelled をローカルの予定が消えたと誤解して弾いてしまう
            // （IsCancelled の下のコメントを見よ）
            CalendarId = existing is not null
                && existing.GoogleCalendarId is { Length: > 0 } confirmedCalendar
                && string.Equals(confirmedCalendar, actualGoogleCalendarId, StringComparison.Ordinal)
                && !string.Equals(existing.CalendarId, calendarId, StringComparison.Ordinal)
                    ? existing.CalendarId
                    : calendarId,

            // Google 側で「いま実際にどこにあるか」は、確かめられた時点で必ず更新する。
            // こちらの希望（CalendarId）と食い違っていれば、次の送信で events.move を使う
            GoogleCalendarId = actualGoogleCalendarId,

            // 表せない繰り返しは null。控えた生データが残るので、書き戻さなければ無傷
            Recurrence = RecurrenceConverter.FromGoogle(element.TextArray("recurrence")),

            Status = element.Text("status"),
            GoogleRaw = GoogleJson.Normalize(element),
            GoogleEventId = googleId,
            GoogleUpdated = element.Text("updated"),
            Source = SourceName,

            // ここへ来た姿（GoogleRaw）が新しい確定した姿。まだ送っていない添付の指定が
            // あったとしても、この呼び出しは「Google から受け取った」場面（取り込み、または
            // 送った直後の応答の取り込み）のどちらかなので、もう保留しておく理由が無い
            PendingAttachments = null,

            UpdatedAt = now ?? DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// 繰り返しのうち1回だけを差し替えたものか。
    /// <para>
    /// Google は繰り返しを<b>親と例外回に分けて</b>持つ。親は <c>RRULE</c> だけを持ち、
    /// 「この回だけ時間を変えた」「この回は中止」は別のイベントとして流れてくる。
    /// これを気づかず取り込むと、<b>同じ日に親の回と例外回が二重に出る</b>。
    /// </para>
    /// </summary>
    public static string? RecurringEventIdOf(JsonElement element) => element.Text("recurringEventId");

    /// <summary>
    /// その回が本来あった日。
    /// <para>親の繰り返しから除く日として使う（<c>EXDATE</c>）。</para>
    /// </summary>
    public static DateOnly? OriginalStartDateOf(JsonElement element)
    {
        if (element.Child("originalStartTime") is not { } original) return null;

        if (original.Text("date") is { } date) return ParseDate(date) is var parsed && parsed != default
            ? parsed
            : null;

        return ParseDateTime(original.Text("dateTime")) is { } moment
            ? DateOnly.FromDateTime(moment.DateTime)
            : null;
    }

    /// <summary>このイベントは取り消されたか。Google は削除を <c>cancelled</c> で流す。</summary>
    public static bool IsCancelled(JsonElement element) =>
        string.Equals(element.Text("status"), CancelledStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 繰り返しのうち1回だけを差し替えた・中止した回（<c>recurringEventId</c> を持つ子）か。
    /// <para>
    /// このような回だけを別のカレンダーへ移すことは Google でもできない（<c>events.move</c>
    /// は独立したイベントにしか使えない）。編集画面はこれを見て、カレンダー欄を
    /// 変えさせないようにする。
    /// </para>
    /// </summary>
    public static bool IsRecurringInstance(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.GoogleRaw is not { Length: > 0 } raw) return false;

        try
        {
            return JsonNode.Parse(raw) is JsonObject original &&
                   original["recurringEventId"] is JsonValue id &&
                   id.TryGetValue<string>(out var text) &&
                   text.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 書き戻す本文を作る。
    /// <para>
    /// <c>patch</c> に渡す前提で、<b>こちらが扱う項目だけ</b>を入れる。欄の無いものは
    /// 入れない（送らなければ Google 側で保たれる）。
    /// </para>
    /// </summary>
    public static JsonObject ToGoogle(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var body = new JsonObject
        {
            ["summary"] = value.Title,

            // null を送ると「消す」意味になる。空欄はそれで正しい
            ["description"] = value.Note,
            ["location"] = value.Location,
            ["start"] = WriteStart(value),
            ["end"] = WriteEnd(value),
        };

        // source は url と title が揃って初めて意味を持つ。片方だけだと Google に弾かれる
        body["source"] = value.Url is { Length: > 0 } url
            ? new JsonObject { ["url"] = url, ["title"] = value.SourceTitle ?? value.Title }
            : null;

        // 空の配列は「繰り返しを外す」。ただし、RDATE・EXRULE など<b>こちらで表せない
        // 繰り返しを控えているだけ</b>の場合に空の配列を送ると、Google 側の繰り返しが
        // 消えてしまう（FromGoogle が Recurrence に null を入れるのは「外れている」時と
        // 「表せない」時の両方で、CalendarEvent 単体では区別できない）。そのときは
        // recurrence キー自体を入れず、PATCH で触らないようにする
        if (value.Recurrence is not null || !HoldsUnrepresentableRecurrence(value))
        {
            var lines = new JsonArray();
            foreach (var line in RecurrenceConverter.BuildOutgoing(value.Recurrence, ReadOriginalRecurrenceLines(value)))
            {
                lines.Add(line);
            }
            body["recurrence"] = lines;
        }

        // 添付は配列まるごとの置き換え。足す・外すという操作をしたときだけ
        // （PendingAttachments が入っているときだけ）このキーを送る
        if (value.PendingAttachments is { Length: > 0 } pending)
        {
            body["attachments"] = ParseAttachmentsJson(pending);
        }

        return body;
    }

    /// <summary>
    /// 添付の一覧。<see cref="CalendarEvent.PendingAttachments"/> があればそれ、無ければ
    /// <see cref="CalendarEvent.GoogleRaw"/> に控えてある Google 側の姿を読む。
    /// <para>編集画面が「いま予定に付いている添付」として出すのはこの一覧。</para>
    /// </summary>
    public static IReadOnlyList<EventAttachment> EffectiveAttachments(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.PendingAttachments is { Length: > 0 } pending)
        {
            return ReadAttachments(ParseAttachmentsJson(pending));
        }

        if (value.GoogleRaw is not { Length: > 0 } raw) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("attachments", out var array) &&
                   array.ValueKind == JsonValueKind.Array
                ? ReadAttachments(array)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>添付の一覧を PATCH／insert に送る形の JSON に直す。</summary>
    public static string ToPendingAttachmentsJson(IReadOnlyList<EventAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var array = new JsonArray();
        foreach (var a in attachments) array.Add(ToAttachmentNode(a));

        return array.ToJsonString();
    }

    private static JsonArray ParseAttachmentsJson(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonArray ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonObject ToAttachmentNode(EventAttachment a) => new()
    {
        ["fileId"] = a.FileId,
        ["fileUrl"] = a.FileUrl,
        ["title"] = a.Title,
        ["mimeType"] = a.MimeType,
        ["iconLink"] = a.IconLink,
    };

    private static IReadOnlyList<EventAttachment> ReadAttachments(JsonNode? array)
    {
        var result = new List<EventAttachment>();
        if (array is not JsonArray items) return result;

        foreach (var item in items)
        {
            if (item is not JsonObject obj) continue;
            if (Text(obj, "fileUrl") is not { } fileUrl) continue;

            result.Add(new EventAttachment(
                Text(obj, "fileId") ?? string.Empty,
                fileUrl,
                Text(obj, "title"),
                Text(obj, "mimeType"),
                Text(obj, "iconLink")));
        }

        return result;
    }

    private static IReadOnlyList<EventAttachment> ReadAttachments(JsonElement array)
    {
        var result = new List<EventAttachment>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.Text("fileUrl") is not { } fileUrl) continue;

            result.Add(new EventAttachment(
                item.Text("fileId") ?? string.Empty,
                fileUrl,
                item.Text("title"),
                item.Text("mimeType"),
                item.Text("iconLink")));
        }

        return result;
    }

    /// <summary>
    /// <see cref="CalendarEvent.Recurrence"/> が null なのは、こちらで本当に繰り返しを
    /// 外したからではなく、<b>表せない繰り返しを生データのまま預かっているだけ</b>か。
    /// <para>
    /// 控えた <see cref="CalendarEvent.GoogleRaw"/> の <c>recurrence</c> を
    /// <see cref="RecurrenceConverter.FromGoogle"/> にもう一度通してみて判断する。
    /// 読み取れるなら（＝本当に外した）false、読み取れない（RDATE・EXRULE・RRULE
    /// 2本以上など）なら true。
    /// </para>
    /// </summary>
    private static bool HoldsUnrepresentableRecurrence(CalendarEvent value)
    {
        if (value.Recurrence is not null) return false;
        if (value.GoogleRaw is not { Length: > 0 } raw) return false;

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject original) return false;
            if (original["recurrence"] is not JsonArray array || array.Count == 0) return false;

            var lines = new List<string>();
            foreach (var item in array)
            {
                if (item is JsonValue text && text.TryGetValue<string>(out var line) && line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return RecurrenceConverter.FromGoogle(lines) is null;
        }
        catch (JsonException)
        {
            // 控えが壊れていたら、これまでどおり空の配列で外す側に倒す
            return false;
        }
    }

    /// <summary>
    /// 控えてある <see cref="CalendarEvent.GoogleRaw"/> の <c>recurrence</c> 行を、
    /// 書式（TZID や区切りなど）をいじらず一字一句そのまま読む。
    /// <para>
    /// <see cref="RecurrenceConverter.BuildOutgoing"/> に渡す。Google 側に本物の
    /// <c>EXDATE</c>（ics 取り込みや他のクライアントが作った繰り返し）があるとき、
    /// それを書き戻しでも一字一句保つため。<see cref="RecurrenceConverter.FromGoogle"/>
    /// を経由すると EXDATE が <c>yyyyMMdd</c> へ丸められ、書式を失ってしまう。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string>? ReadOriginalRecurrenceLines(CalendarEvent value)
    {
        if (value.GoogleRaw is not { Length: > 0 } raw) return null;

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject original) return null;
            if (original["recurrence"] is not JsonArray array || array.Count == 0) return null;

            var lines = new List<string>();
            foreach (var item in array)
            {
                if (item is JsonValue text && text.TryGetValue<string>(out var line) && line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines.Count > 0 ? lines : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Google 側で内容を変えられない予定か。
    /// <para>
    /// こちらで編集させてしまうと、保存はできるのに向こうへ伝わらない。画面から
    /// 変えさせないために、表示側もこれを見る。
    /// </para>
    /// </summary>
    public static bool IsLocked(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.GoogleRaw is not { Length: > 0 } raw) return false;

        try
        {
            return JsonNode.Parse(raw) is JsonObject original && IsLockedOnGoogle(original);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Google 側で内容を変えられない予定か。
    /// <para>
    /// メールから起こされた予約（ホテルや美容室など）、誕生日、勤務場所がこれにあたる。
    /// Google はこれらに <c>locked</c> を立てて返し、書き換えようとすると断る。
    /// こちらから消すことはできるので、送らないだけにして同期からは外さない。
    /// </para>
    /// </summary>
    private static bool IsLockedOnGoogle(JsonObject original)
    {
        if (original["locked"] is JsonValue locked && locked.TryGetValue<bool>(out var isLocked) && isLocked)
        {
            return true;
        }

        if (original["eventType"] is JsonValue kind
            && kind.TryGetValue<string>(out var type)
            && type is "fromGmail" or "birthday" or "workingLocation")
        {
            return true;
        }

        return IsOrganizedByOther(original);
    }

    /// <summary>
    /// 他人が主催する予定か。
    /// <para>
    /// 主催者でなければ、こちらで内容を変えても Google 側は拒む（<c>guestsCanModify</c>
    /// が立っていれば別）。編集させても保存はできるのに向こうへ伝わらないので、
    /// 送れない予定として扱う。
    /// </para>
    /// <para>
    /// 条件は<b>「<c>organizer.self</c> が明示的に false で、かつ <c>guestsCanModify</c> が
    /// true でない」</b>。<c>organizer</c> が無いとき、<c>organizer.self</c> が無いときは
    /// 止めない。省略されていることが実際にあり、そこまで巻き込むと自分の予定まで
    /// 編集できなくなる。
    /// </para>
    /// </summary>
    private static bool IsOrganizedByOther(JsonObject original)
    {
        if (original["organizer"] is not JsonObject organizer) return false;
        if (organizer["self"] is not JsonValue selfFlag || !selfFlag.TryGetValue<bool>(out var isSelf)) return false;
        if (isSelf) return false;

        var guestsCanModify = original["guestsCanModify"] is JsonValue modify
            && modify.TryGetValue<bool>(out var canModify)
            && canModify;

        return !guestsCanModify;
    }

    /// <summary>
    /// 書き戻す必要があるか。
    /// <para>
    /// 控えた生データに、こちらの内容を当てたものと見比べる。同じなら送らない。
    /// 毎回送ると更新時刻が動き、相手側でも「変わった」と見えてしまう。
    /// </para>
    /// </summary>
    public static bool NeedsPush(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // 一度も受け取っていなければ、比べる相手が無い
        if (value.GoogleRaw is not { Length: > 0 } raw) return true;

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject original) return true;

            // 向こうで内容を変えられない予定は送らない。何度送っても断られるだけで、
            // そのたびに「一部を伝えられません」と出る
            if (IsLockedOnGoogle(original)) return false;

            var wanted = ToGoogle(value);
            foreach (var pair in wanted)
            {
                // 開始と終了は書き方の揺れが大きい。文字列ではなく時刻として比べる
                var same = pair.Key is "start" or "end"
                    ? SameMoment(pair.Value, original[pair.Key])
                    : Same(pair.Value, original[pair.Key]);

                if (!same) return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // 控えが壊れていたら送り直す
            return true;
        }
    }

    /// <summary>
    /// 送ろうとしている値と、控えてある値が同じか。
    /// <para>
    /// <b>「無い」と「空」を同じものとして扱う。</b>Google は繰り返しでない予定に
    /// <c>recurrence</c> を返さず、場所や説明が空なら項目ごと返さない。こちらは
    /// 「繰り返さない」を空の配列で、「空欄」を null で表すので、そのまま比べると
    /// <b>毎回「変わった」と判定して送り返してしまう</b>。
    /// </para>
    /// </summary>
    private static bool Same(JsonNode? wanted, JsonNode? original) =>
        IsBlank(wanted) && IsBlank(original) ||
        GoogleJson.SameContent(wanted?.ToJsonString(), original?.ToJsonString());

    /// <summary>
    /// 開始・終了が同じ時刻を指しているか。
    /// <para>
    /// <b>文字列で比べない。</b>同じ時刻でも <c>+09:00</c> と <c>Z</c>、小数秒の桁、
    /// <c>timeZone</c> の有無で書き方が変わる。そのまま比べると、変えていないのに
    /// 毎回送り返すことになる。
    /// </para>
    /// </summary>
    private static bool SameMoment(JsonNode? wanted, JsonNode? original)
    {
        if (wanted is not JsonObject left || original is not JsonObject right)
        {
            return Same(wanted, original);
        }

        // 終日かどうかが違えば、それは変更
        var leftDate = Text(left, "date");
        var rightDate = Text(right, "date");

        if (leftDate is not null || rightDate is not null)
        {
            return string.Equals(leftDate, rightDate, StringComparison.Ordinal);
        }

        var leftMoment = ParseDateTime(Text(left, "dateTime"));
        var rightMoment = ParseDateTime(Text(right, "dateTime"));

        return leftMoment is not null && rightMoment is not null
            ? leftMoment.Value.ToUniversalTime() == rightMoment.Value.ToUniversalTime()
            : Same(wanted, original);
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : null;

    /// <summary>中身が無いとみなせるか。null、空の配列、空文字。</summary>
    private static bool IsBlank(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue value => value.TryGetValue<string>(out var text) && string.IsNullOrEmpty(text),
        _ => false,
    };

    // ------------------------------------------------------------------
    // 日付と時刻
    // ------------------------------------------------------------------

    /// <summary>開始を読む。<c>date</c> なら終日、<c>dateTime</c> なら時刻つき。</summary>
    private static (DateOnly Date, TimeOnly? Time) ReadStart(JsonElement? start)
    {
        if (start is not { } value) return (default, null);

        if (value.Text("date") is { } date) return (ParseDate(date), null);

        if (ParseDateTime(value.Text("dateTime")) is { } moment)
        {
            // この PC の時刻に直す。書き戻すときもこの PC の時差を付けるので、
            // ここで合わせておかないと往復するたびに時刻がずれる
            var local = moment.ToLocalTime();
            return (DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime));
        }

        return (default, null);
    }

    /// <summary>
    /// 終了を読む。
    /// <para>
    /// 終日予定の <c>end.date</c> は<b>その日を含まない</b>。1日引いて、こちらの持ち方に直す。
    /// </para>
    /// </summary>
    private static (DateOnly? Date, TimeOnly? Time) ReadEnd(JsonElement? end, DateOnly start, bool timed)
    {
        if (end is not { } value) return (null, null);

        if (value.Text("date") is { } date)
        {
            // 排他（終了の翌日）で届くので1日戻す
            var last = ParseDate(date).AddDays(-1);

            // 単日なら EndDate は持たない。開始日と同じ値を入れても情報が増えない
            return (last > start ? last : null, null);
        }

        if (ParseDateTime(value.Text("dateTime")) is not { } moment) return (null, null);

        var local = moment.ToLocalTime();
        var endDate = DateOnly.FromDateTime(local.DateTime);
        var endTime = TimeOnly.FromDateTime(local.DateTime);

        // 時刻つきで日をまたぐ予定。終了日を持たせないと1日目だけの予定に見える
        return (timed && endDate > start ? endDate : null, endTime);
    }

    private static JsonObject WriteStart(CalendarEvent value)
    {
        if (value.IsAllDay) return new JsonObject { ["date"] = Format(value.Date) };

        var body = new JsonObject { ["dateTime"] = FormatDateTime(value.Date, value.StartTime!.Value) };
        if (LocalIanaTimeZoneId.Value is { } zone) body["timeZone"] = zone;

        return body;
    }

    /// <summary>
    /// 終日予定は<b>翌日</b>を送る。Google 側が排他で解釈するため。
    /// <para>
    /// 時刻付きの予定では、<b>終了が開始より後であることを必ず満たす</b>。Google は
    /// 長さ 0 の範囲も逆転した範囲も受け付けず、400（badRequest）で断る。以前は
    /// 終了時刻が無い予定を「開始と同じ時刻」で送っていたので、そういう予定が1件でも
    /// あると送信が断られ、そのカレンダーの同期が丸ごと止まっていた。
    /// </para>
    /// <para>
    /// 終了時刻を持たない予定は1時間として送る。編集画面の既定（9:00〜10:00）と
    /// 同じ長さで、送ったあとは相手からその終了時刻が戻ってくる。
    /// </para>
    /// </summary>
    private static JsonObject WriteEnd(CalendarEvent value)
    {
        if (value.IsAllDay) return new JsonObject { ["date"] = Format(value.LastDate.AddDays(1)) };

        var startAt = value.Date.ToDateTime(value.StartTime!.Value, DateTimeKind.Unspecified);

        var endAt = value.EndTime is { } endTime
            ? (value.EndDate ?? value.Date).ToDateTime(endTime, DateTimeKind.Unspecified)
            : startAt.AddHours(1);

        if (endAt <= startAt) endAt = startAt.AddHours(1);

        var body = new JsonObject
        {
            ["dateTime"] = FormatDateTime(DateOnly.FromDateTime(endAt), TimeOnly.FromDateTime(endAt)),
        };
        if (LocalIanaTimeZoneId.Value is { } zone) body["timeZone"] = zone;

        return body;
    }

    /// <summary>
    /// この PC の時差の IANA 名（例 <c>Asia/Tokyo</c>）。得られなければ null。
    /// <para>
    /// Google Calendar の Events リファレンスは、<b>繰り返し予定では
    /// <c>start.timeZone</c> / <c>end.timeZone</c> が必須</b>と明記している。単発の予定に
    /// だけ付けて繰り返しには付け忘れる、という抜けを作らないよう、<b>時刻つきの予定なら
    /// 繰り返しの有無を問わず常に添える</b>ほうが単純で安全と判断した。
    /// </para>
    /// <para>
    /// Windows は「Tokyo Standard Time」のような独自の ID を持つので、
    /// <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId"/> で IANA 名に変換する。
    /// Linux ではもとから IANA 名（<see cref="TimeZoneInfo.HasIanaId"/>）なのでそのまま使う。
    /// <b>変換できなければ null を返し、呼び出し側は timeZone を添えずにこれまでどおり送る。</b>
    /// </para>
    /// <para>
    /// 一度求めれば同じ実行中は変わらないので、プロセス内で使い回す。
    /// </para>
    /// </summary>
    private static readonly Lazy<string?> LocalIanaTimeZoneId = new(() =>
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    });

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : default;

    private static DateTimeOffset? ParseDateTime(string? text) =>
        text is not null && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// RFC3339 で書く。時差はこの PC の設定に従う。
    /// <para>予定を入れるのも見るのも同じ場所なので、端末の時差で書けば食い違わない。</para>
    /// </summary>
    private static string FormatDateTime(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
