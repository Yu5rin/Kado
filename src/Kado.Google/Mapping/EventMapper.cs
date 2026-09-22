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
    /// <param name="calendarId">取りに行ったカレンダーの ID。</param>
    /// <param name="existing">すでに持っている同じ予定。初回なら null。</param>
    /// <param name="now">ローカルの更新時刻に入れる値。</param>
    public static CalendarEvent FromGoogle(
        JsonElement element,
        string calendarId,
        CalendarEvent? existing = null,
        DateTimeOffset? now = null)
    {
        var googleId = element.Text("id")
            ?? throw new ArgumentException("id の無いイベントは読めません。", nameof(element));

        var start = element.Child("start");
        var end = element.Child("end");

        var (date, startTime) = ReadStart(start);
        var (endDate, endTime) = ReadEnd(end, date, startTime is not null);

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
            CalendarId = calendarId,

            // 表せない繰り返しは null。控えた生データが残るので、書き戻さなければ無傷
            Recurrence = RecurrenceConverter.FromGoogle(element.TextArray("recurrence")),

            Status = element.Text("status"),
            GoogleRaw = GoogleJson.Normalize(element),
            GoogleEventId = googleId,
            GoogleUpdated = element.Text("updated"),
            Source = SourceName,
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

        // 空の配列は「繰り返しを外す」。null だと「触らない」になってしまう
        var recurrence = RecurrenceConverter.ToGoogle(value.Recurrence);
        var lines = new JsonArray();
        foreach (var line in recurrence) lines.Add(line);
        body["recurrence"] = lines;

        return body;
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

        return original["eventType"] is JsonValue kind
            && kind.TryGetValue<string>(out var type)
            && type is "fromGmail" or "birthday" or "workingLocation";
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

    private static JsonObject WriteStart(CalendarEvent value) =>
        value.IsAllDay
            ? new JsonObject { ["date"] = Format(value.Date) }
            : new JsonObject { ["dateTime"] = FormatDateTime(value.Date, value.StartTime!.Value) };

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

        return new JsonObject
        {
            ["dateTime"] = FormatDateTime(DateOnly.FromDateTime(endAt), TimeOnly.FromDateTime(endAt)),
        };
    }

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
