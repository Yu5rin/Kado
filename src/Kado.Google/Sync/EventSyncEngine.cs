using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;

namespace Kado.Google.Sync;

/// <summary>
/// 予定について、Google 側へ出入りする口。
/// <para>実物は <see cref="GoogleCalendarApi"/>。試験では差し替える。</para>
/// </summary>
public interface IEventGateway
{
    Task<GooglePage> ListAsync(
        string calendarId, string? syncToken, string? pageToken, CancellationToken cancellationToken);

    /// <summary>
    /// イベントを1件取る。無ければ 404（<see cref="GoogleApiException.IsMissing"/>）。
    /// 移す指示を出していた予定が、移し先に本当にあるかを確かめるのに使う。
    /// </summary>
    Task<JsonElement> GetAsync(string calendarId, string eventId, CancellationToken cancellationToken);

    Task<JsonElement> InsertAsync(string calendarId, JsonObject body, CancellationToken cancellationToken);

    Task<JsonElement> PatchAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken);

    Task DeleteAsync(string calendarId, string eventId, CancellationToken cancellationToken);

    /// <summary>
    /// イベントを別のカレンダーへ移す。消して作り直すより、ゲスト・会議 URL・添付・色が
    /// 保たれる。
    /// </summary>
    Task<JsonElement> MoveAsync(
        string sourceCalendarId, string eventId, string destinationCalendarId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 全件を取るときの期間の始まり（<c>timeMin</c>）。無ければ全期間。
    /// <para>
    /// 全件を取り直したときに「一覧に無いから消えた」と判断してよいのは、この期間の予定だけ。
    /// 取っていない期間の予定まで消さないために、同期の側が見る。
    /// </para>
    /// </summary>
    DateTimeOffset? ListFrom => null;
}

/// <summary><see cref="GoogleCalendarApi"/> を上の口に合わせる。</summary>
public sealed class CalendarApiGateway(GoogleCalendarApi api, DateTimeOffset? from = null) : IEventGateway
{
    public DateTimeOffset? ListFrom => from;

    public Task<GooglePage> ListAsync(
        string calendarId, string? syncToken, string? pageToken, CancellationToken cancellationToken) =>
        api.ListEventsAsync(calendarId, syncToken, pageToken, from, cancellationToken);

    public Task<JsonElement> GetAsync(
        string calendarId, string eventId, CancellationToken cancellationToken) =>
        api.GetEventAsync(calendarId, eventId, cancellationToken);

    public Task<JsonElement> InsertAsync(
        string calendarId, JsonObject body, CancellationToken cancellationToken) =>
        api.InsertEventAsync(calendarId, body, cancellationToken);

    public Task<JsonElement> PatchAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken) =>
        api.PatchEventAsync(calendarId, eventId, body, cancellationToken);

    public Task DeleteAsync(string calendarId, string eventId, CancellationToken cancellationToken) =>
        api.DeleteEventAsync(calendarId, eventId, cancellationToken);

    public Task<JsonElement> MoveAsync(
        string sourceCalendarId, string eventId, string destinationCalendarId,
        CancellationToken cancellationToken) =>
        api.MoveEventAsync(sourceCalendarId, eventId, destinationCalendarId, cancellationToken);
}

/// <summary>
/// 予定の同期。
/// <para>
/// 順番は<b>削除を伝える → 取り込む → 送る</b>。削除を先に伝えないと、消したものが
/// 取り込みで復活する。取り込みを送信より先にするのは、衝突したとき相手を採る方針に
/// 合わせるため（<see cref="SyncPolicy"/>）。
/// </para>
/// </summary>
public sealed class EventSyncEngine(
    EventRepository events,
    TombstoneRepository tombstones,
    SettingsRepository settings,
    IEventGateway gateway,
    TimeProvider? clock = null)
{
    /// <summary>1回の同期で回すページ数の上限。無限に回らないようにする。</summary>
    private const int MaxPages = 50;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// このカレンダーの差分の印を覚えておくキー。
    /// <para>印を捨てて全件取り直させたいとき（一覧から外れて戻った、など）に、呼び出し側も使う。</para>
    /// </summary>
    public static string TokenKey(string calendarId) => $"calendar:{calendarId}:syncToken";

    /// <summary>
    /// 同期する。
    /// </summary>
    /// <param name="calendarId">Google 側のカレンダー ID。</param>
    /// <param name="localCalendarId">こちらのカレンダー ID。取り込んだ予定の所属になる。</param>
    /// <param name="readOnly">
    /// 読むだけにするか。
    /// <para>
    /// 祝日や誕生日、他人から共有されたカレンダーは<b>こちらから書けない</b>。
    /// 送ろうとすると断られるので、はじめから送らない。
    /// </para>
    /// </param>
    public async Task<SyncReport> SyncAsync(
        string calendarId, string localCalendarId,
        CancellationToken cancellationToken = default, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);

        var report = readOnly
            ? new SyncReport()
            : await PushDeletionsAsync(calendarId, cancellationToken).ConfigureAwait(false);

        report += await PullAsync(calendarId, localCalendarId, cancellationToken).ConfigureAwait(false);

        if (!readOnly)
        {
            report += await PushChangesAsync(calendarId, localCalendarId, cancellationToken)
                .ConfigureAwait(false);
        }

        return report;
    }

    // ------------------------------------------------------------------
    // 削除を伝える。取り込みより先に行う。あとだと消したものが復活する
    // ------------------------------------------------------------------

    private async Task<SyncReport> PushDeletionsAsync(string calendarId, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var warnings = new List<string>();

        // このカレンダーのものだけ。絞らないと、他のカレンダーの予定まで
        // ここへ投げることになる
        foreach (var tombstone in tombstones.Pending(TombstoneRepository.EventKind, calendarId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await gateway.DeleteAsync(calendarId, tombstone.GoogleId!, cancellationToken).ConfigureAwait(false);

                // 伝え終わったら記録を消す。残すと ID の再利用で取り違える
                tombstones.Clear(tombstone.Id, TombstoneRepository.EventKind);
                deleted++;
            }
            catch (GoogleApiException ex) when (ex.IsMissing || ex.Status == HttpStatusCode.Gone)
            {
                // このカレンダーには無い。持ち主が分かっているなら、すでに消えている
                // ということなので記録を片付ける。
                //
                // 持ち主が分からない古い記録は、ここで片付けてはいけない。別の
                // カレンダーに残っているかもしれず、捨てると<b>削除が永久に届かない</b>。
                // 残しておけば次のカレンダーが拾う。どこにも無ければ Prune が片付ける
                if (tombstone.SourceId is { Length: > 0 })
                {
                    tombstones.Clear(tombstone.Id, TombstoneRepository.EventKind);
                }
            }
            catch (GoogleApiException ex) when (ex.IsPermissionDenied)
            {
                // 権限が無い（読み取り専用になった、主催者でない、など）。出し直しても通らないので、
                // 記録を残すと同期のたびに同じ失敗を繰り返す。捨てて、使う人には知らせる
                tombstones.Clear(tombstone.Id, TombstoneRepository.EventKind);
                warnings.Add(
                    $"権限が無いため、Google 側の予定を削除できません（{tombstone.Id}）: {ex.Reason}。" +
                    "手元からは消えていますが、Google には残っています");
            }
            catch (GoogleApiException ex)
            {
                // 呼びすぎ・サーバー側の不調と、その他の 4xx。1件の削除が断られただけで、
                // このカレンダーの取り込みも送信も止めない。記録は残し、次の同期でやり直す
                warnings.Add(ex.Description is { Length: > 0 } detail
                    ? $"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason} — {detail}"
                    : $"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason}");
            }
        }

        return new SyncReport { DeletedRemote = deleted, Warnings = warnings };
    }

    // ------------------------------------------------------------------
    // 取り込む
    // ------------------------------------------------------------------

    private async Task<SyncReport> PullAsync(
        string calendarId, string localCalendarId, CancellationToken cancellationToken)
    {
        var token = settings.GetSyncState(TokenKey(calendarId));
        var fullResync = false;

        // 印が無い（初回・繋ぎ直し・印を捨てた）ときも、全件を取る。差分と違い、消えた予定は
        // cancelled では降ってこない（showDeleted=false）ので、一覧に無いことで見つける
        var fullListing = token is not { Length: > 0 };

        List<JsonElement> items;
        string? nextToken;
        bool complete;

        try
        {
            (items, nextToken, complete) =
                await ReadAllPagesAsync(calendarId, token, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (ex.NeedsFullResync)
        {
            // 差分では追いつけない。印を捨てて全部取り直す（要件書 6.3）
            fullResync = true;
            fullListing = true;
            settings.SetSyncState(TokenKey(calendarId), string.Empty);

            (items, nextToken, complete) =
                await ReadAllPagesAsync(calendarId, null, cancellationToken).ConfigureAwait(false);
        }

        var created = 0;
        var updated = 0;
        var deleted = 0;
        var relinked = 0;
        var now = _clock.GetUtcNow();
        var overwritten = new List<string>();
        var warnings = new List<string>();

        // 例外回は必ず親のあとに処理する。先に処理すると、そのあと親を取り込んだときに
        // 足した除外日が消え、同じ日に二重に出たままになる
        foreach (var item in items.OrderBy(i => EventMapper.RecurringEventIdOf(i) is null ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GoogleJson.Text(item, "id") is not { } googleId) continue;

            var existing = events.FindByGoogleId(googleId);

            // 繰り返しのうち1回だけを差し替えたもの。親からその日を除かないと、
            // 同じ日に親の回と例外回が二重に出る
            if (EventMapper.RecurringEventIdOf(item) is { Length: > 0 } parentId)
            {
                if (ExcludeFromParent(parentId, EventMapper.OriginalStartDateOf(item), now)) updated++;

                if (EventMapper.IsCancelled(item))
                {
                    // その回は中止。親から除いたので、予定としては持たない。
                    // ただし、こちらがすでに別のカレンダーへ移したと分かっているなら、
                    // この cancelled は「削除された」ではなく「(移す前の)ここから居なくなった」
                    // だけの合図。誤って消さない（ShouldDeleteOnCancel を見よ）
                    if (ShouldDeleteOnCancel(existing, calendarId) &&
                        await DeleteLocalAsync(existing!, calendarId, now, warnings, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        deleted++;
                    }

                    continue;
                }
            }

            if (EventMapper.IsCancelled(item))
            {
                // 相手が消した。こちらでも消すが、tombstone は要らない。
                // 相手はもう知っている。残すと次の同期で消しに行ってしまう。
                //
                // ただし、このカレンダーからの cancelled は「利用者が削除した」だけとは
                // 限らない。こちらで入れ先を別のカレンダーへ変え、すでに events.move で
                // 移したあとにも、元のカレンダー（このカレンダー）側では同じ知らせが返る
                // （ShouldDeleteOnCancel を見よ）
                if (ShouldDeleteOnCancel(existing, calendarId) &&
                    await DeleteLocalAsync(existing!, calendarId, now, warnings, cancellationToken)
                        .ConfigureAwait(false))
                {
                    deleted++;
                }

                continue;
            }

            // こちらで消したものは復活させない
            if (tombstones.ContainsGoogleId(googleId, TombstoneRepository.EventKind)) continue;

            // 相手が変わっていなければ触らない。全部取り直したときに、まだ送れていない
            // こちらの変更を押し潰してしまう。相手が変わっていれば、方針どおり相手を採る
            if (existing is not null &&
                GoogleJson.SameContent(existing.GoogleRaw, GoogleJson.Normalize(item)))
            {
                // 「Google 上で見つからない」印が付いていたものが、また見つかった（別の
                // カレンダーにいた、など）。内容は触らず、印だけを外して結び直す
                if (existing.GoogleMissing)
                {
                    events.Upsert(existing with { GoogleMissing = false, GoogleCalendarId = calendarId });
                    relinked++;
                }

                continue;
            }

            // Google を採る方針は変えないが、こちらにまだ送れていない編集があるなら
            // それを黙って捨てることになる。気づけるよう警告に残す
            if (existing is not null && EventMapper.NeedsPush(existing))
            {
                overwritten.Add(existing.Title is { Length: > 0 } title ? title : "(無題)");
            }

            var mapped = EventMapper.FromGoogle(item, localCalendarId, existing, now, calendarId);

            if (existing is null)
            {
                // 同じ予定が2件に増えないよう、結び付いていないものを探して引き受ける
                if (FindUnlinkedMatch(mapped, localCalendarId) is { } orphan)
                {
                    events.Upsert(mapped with { Id = orphan.Id });
                    updated++;
                    continue;
                }

                events.Upsert(mapped);
                created++;
            }
            else
            {
                events.Upsert(mapped);
                updated++;
            }
        }

        // 全件を取り直したのに、一覧に無い（Google で消えた）予定を片付ける。
        // 一覧を最後まで読めたときだけ行う。途中で打ち切った一覧で見ると、読めなかっただけの
        // 予定を消してしまう
        if (fullListing && complete)
        {
            deleted += RemoveVanished(calendarId, localCalendarId, items, warnings);
        }

        if (nextToken is { Length: > 0 }) settings.SetSyncState(TokenKey(calendarId), nextToken);

        return new SyncReport
        {
            CreatedLocal = created,
            UpdatedLocal = updated,
            DeletedLocal = deleted,
            Relinked = relinked,
            FullResync = fullResync,
            Warnings = [.. warnings, .. SummarizeOverwritten(overwritten)],
        };
    }

    /// <summary>
    /// 相手の取り消し（cancelled）を受けて、こちらの行を消す。
    /// <para>
    /// <b>消す前に2つ確かめる。</b>
    /// </para>
    /// <para>
    /// 1つ目。こちらで入れ先を変えて移す指示を出していて（<see cref="CalendarEvent.CalendarId"/> と
    /// <see cref="CalendarEvent.GoogleCalendarId"/> が食い違う）、その cancelled が元のカレンダーから
    /// 来たときは、「移し先に確かにあるか」を見る。<c>events.move</c> が Google で通ったのに応答を
    /// 受け取る前に切れると、手元はまだ元のカレンダーにいると思っている。そこへ元からの
    /// cancelled が来て消すと、移し先に生きている予定を手元から失い、未送信の編集も一緒に消える。
    /// あれば「移動は済んでいた」として入れ先だけを直し、消さない。
    /// </para>
    /// <para>
    /// 2つ目。こちらに未送信の編集があるまま消すときは、捨てたことを警告に残す。
    /// 黙って消えると、Google 側で消されたことにも自分の編集が無くなったことにも気づけない。
    /// </para>
    /// </summary>
    /// <returns>消したら true。</returns>
    private async Task<bool> DeleteLocalAsync(
        CalendarEvent existing, string calendarId, DateTimeOffset now, List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (await IsAlreadyMovedAsync(existing, cancellationToken).ConfigureAwait(false))
        {
            events.Upsert(existing with { GoogleCalendarId = existing.CalendarId, UpdatedAt = now });
            return false;
        }

        if (HasUnsentChanges(existing))
        {
            warnings.Add(
                "Google 側で削除されたため、こちらの未送信の変更を捨てました：" +
                (existing.Title is { Length: > 0 } title ? title : "(無題)"));
        }

        return events.Delete(existing.Id);
    }

    /// <summary>まだ Google に送っていない編集（内容・添付）を持っているか。</summary>
    private static bool HasUnsentChanges(CalendarEvent value) =>
        EventMapper.NeedsPush(value) || value.PendingAttachments is not null;

    /// <summary>
    /// 移す指示を出していた予定が、移し先に本当にあるか。
    /// <para>
    /// 移す指示が無ければ（入れ先の希望と実際の場所が同じ、または実際の場所が分からない）確かめない。
    /// 移し先が見つからない・調べられない種類の失敗（宛先が Google のものではない、など）は
    /// 「ない」として扱う。出し直せば直る失敗（呼びすぎ・サーバー側）だけは上へ返し、次の同期で
    /// cancelled をもう一度受け取って判断し直す。
    /// </para>
    /// </summary>
    private async Task<bool> IsAlreadyMovedAsync(CalendarEvent existing, CancellationToken cancellationToken)
    {
        if (existing.GoogleEventId is not { Length: > 0 } googleId) return false;
        if (existing.GoogleCalendarId is not { Length: > 0 } origin) return false;
        if (existing.CalendarId is not { Length: > 0 } destination) return false;
        if (string.Equals(origin, destination, StringComparison.Ordinal)) return false;

        try
        {
            var found = await gateway.GetAsync(destination, googleId, cancellationToken).ConfigureAwait(false);

            // 消された予定は 404 ではなく cancelled で返ることがある。それは「ある」ではない
            return !EventMapper.IsCancelled(found);
        }
        catch (GoogleApiException ex) when (!ex.IsTransient)
        {
            return false;
        }
    }

    /// <summary>
    /// 未送信の編集を捨てて Google 側を採った予定を、警告文にまとめる。
    /// <para>
    /// 件数が多いと画面が埋まるので、名前を出すのは数件までにして残りは件数だけ添える。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> SummarizeOverwritten(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0) return [];

        const int maxNamed = 3;
        var named = string.Concat(titles.Take(maxNamed).Select(title => $"「{title}」"));

        var message = titles.Count > maxNamed
            ? $"{named}ほか{titles.Count - maxNamed}件は、こちらの変更を捨てて Google 側を採りました"
            : $"{named}は、こちらの変更を捨てて Google 側を採りました";

        return [message];
    }

    private async Task<(List<JsonElement> Items, string? NextSyncToken, bool Complete)> ReadAllPagesAsync(
        string calendarId, string? syncToken, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? pageToken = null;
        string? nextSyncToken = null;
        var complete = false;

        for (var page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await gateway
                .ListAsync(calendarId, syncToken, pageToken, cancellationToken)
                .ConfigureAwait(false);

            items.AddRange(result.Items);

            // 印は最後のページにだけ付く
            if (result.NextSyncToken is { Length: > 0 }) nextSyncToken = result.NextSyncToken;

            pageToken = result.NextPageToken;

            if (pageToken is not { Length: > 0 })
            {
                complete = true;
                break;
            }
        }

        return (items, nextSyncToken, complete);
    }

    /// <summary>
    /// 全件を取り直した一覧に無い、Google と結び付いた予定を片付ける。
    /// <para>
    /// syncToken の期限切れ（410）や繋ぎ直しで全件を取り直すと、その間に Google で消えた予定は
    /// <c>cancelled</c> で降ってこない。手元に残ったままになる。
    /// </para>
    /// <para>
    /// <b>消してよいのは、一覧が取った範囲の予定だけ。</b>期間を切って取っているとき
    /// （<see cref="IEventGateway.ListFrom"/>）、それより前の予定は一覧に載らないだけで消えていない。
    /// 繰り返しは、その期間に届くとはっきり言えるものだけ見る。
    /// </para>
    /// <para>
    /// 未送信の変更があるものは消さない。Google で見つからない印（<see cref="CalendarEvent.GoogleMissing"/>）を
    /// 付けて残し、警告に出す（以後は黙って作り直さない）。
    /// </para>
    /// </summary>
    /// <returns>手元から消した件数。</returns>
    private int RemoveVanished(
        string calendarId, string localCalendarId, List<JsonElement> listed, List<string> warnings)
    {
        var listedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in listed)
        {
            if (GoogleJson.Text(item, "id") is { } id) listedIds.Add(id);
        }

        var from = gateway.ListFrom;
        var removed = 0;
        var kept = new List<string>();

        foreach (var value in events.All())
        {
            if (value.GoogleEventId is not { Length: > 0 } googleId) continue;
            if (listedIds.Contains(googleId) || value.GoogleMissing) continue;
            if (!LivesIn(value, calendarId, localCalendarId)) continue;
            if (!IsWithinListedRange(value, from)) continue;

            // 未送信の変更（内容・添付・別のカレンダーへ移す指示）があるなら、捨てない
            if (HasUnsentChanges(value) || NeedsMove(value, calendarId))
            {
                events.Upsert(value with { GoogleMissing = true });
                kept.Add(value.Title is { Length: > 0 } title ? title : "(無題)");
                continue;
            }

            if (events.Delete(value.Id)) removed++;
        }

        if (kept.Count > 0)
        {
            const int maxNamed = 3;
            var named = string.Concat(kept.Take(maxNamed).Select(title => $"「{title}」"));

            warnings.Add(
                (kept.Count > maxNamed ? $"{named}ほか{kept.Count - maxNamed}件" : named) +
                "は Google で見つかりません。まだ送っていない変更があるため残し、以後は送りません" +
                "（編集画面から「Google に新しく作り直す」を選べます）");
        }

        return removed;
    }

    /// <summary>
    /// Google 側でいま実際にこのカレンダーにある予定か。
    /// 場所をまだ確かめていない旧いデータは、入れ先の希望で見る。
    /// </summary>
    private static bool LivesIn(CalendarEvent value, string calendarId, string localCalendarId) =>
        value.GoogleCalendarId is { Length: > 0 } actual
            ? string.Equals(actual, calendarId, StringComparison.Ordinal)
            : string.Equals(value.CalendarId, localCalendarId, StringComparison.Ordinal);

    /// <summary>
    /// 一覧が取った範囲に、この予定が入っているはずか。
    /// <para>
    /// 期間を切っていなければ、すべて入っている。切っているときは、終わりがその期間より後の
    /// 予定（Google の <c>timeMin</c> は「終了時刻がこれより後」）。日付の境目の揺れを見て、
    /// 前後1日ぶんは余裕を持たせて「入っていない」側に倒す。
    /// </para>
    /// </summary>
    private static bool IsWithinListedRange(CalendarEvent value, DateTimeOffset? from)
    {
        if (from is not { } start) return true;

        var limit = DateOnly.FromDateTime(start.UtcDateTime).AddDays(2);

        // 繰り返しは、期間に届くとはっきり言えるものだけ。Kado で表せない繰り返しは判断できない
        if (value.IsRecurring) return RecurrenceReaches(value.Recurrence!, limit);
        if (EventMapper.HoldsUnrepresentableRecurrence(value)) return false;

        return value.LastDate >= limit;
    }

    /// <summary>繰り返しが、その日以降にも続くか。回数で終わるものは数えられないので false。</summary>
    private static bool RecurrenceReaches(string spec, DateOnly limit)
    {
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase)) return false;

            if (part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase))
            {
                var text = part["UNTIL=".Length..];

                return text.Length >= 8 && DateOnly.TryParseExact(
                           text[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                           System.Globalization.DateTimeStyles.None, out var until)
                       && until >= limit;
            }
        }

        // 終わりが無い
        return true;
    }

    /// <summary>
    /// 親の繰り返しからその日を除く。
    /// <para>
    /// 親がこちらに無いこともある（差分で例外回だけが降ってきた、親がまだ取り込まれて
    /// いない）。そのときは何もしない。次に親が降りてくれば、その時点の例外回で除かれる。
    /// </para>
    /// </summary>
    /// <returns>除いて書き換えたら true。</returns>
    private bool ExcludeFromParent(string parentGoogleId, DateOnly? date, DateTimeOffset now)
    {
        if (date is not { } day) return false;
        if (events.FindByGoogleId(parentGoogleId) is not { } parent) return false;

        var updated = RecurrenceConverter.WithExceptionDate(parent.Recurrence, day);

        // すでに除いてあれば触らない。毎回書き換えると更新時刻が動く
        if (string.Equals(updated, parent.Recurrence, StringComparison.Ordinal)) return false;

        events.Upsert(parent with { Recurrence = updated, UpdatedAt = now });
        return true;
    }

    /// <summary>
    /// このカレンダーからの <c>cancelled</c>（取り消し）を、削除として受け取ってよいか。
    /// <para>
    /// cancelled は「利用者が削除した」以外に、「こちらの入れ先を別のカレンダーへ変え、
    /// <c>events.move</c> で運んだ結果、元のカレンダー側にはもう無い」場面でも届く。
    /// 後者を削除として扱うと、移した予定を消してしまう。
    /// </para>
    /// <para>
    /// 判定は、Google 側で最後に確かめた場所（<see cref="CalendarEvent.GoogleCalendarId"/>）が
    /// 分かっていて、かつそれが<b>今回の（cancelled を返してきた）カレンダーと違う</b>とき
    /// だけ、削除しない。まだ場所を確かめていない（<c>GoogleCalendarId</c> が null。
    /// 旧いデータや、移す指示が届く前）ときは、これまでどおり削除する側に倒す
    /// （こちらで移す指示がまだ送れていなくても、Google 側で本当に消されたなら消える。
    /// 移し先で作り直して生き返らせない）。
    /// </para>
    /// </summary>
    private static bool ShouldDeleteOnCancel(CalendarEvent? existing, string calendarId) =>
        existing is not null &&
        (existing.GoogleCalendarId is not { Length: > 0 } known ||
         string.Equals(known, calendarId, StringComparison.Ordinal));

    /// <summary>
    /// まだ結び付いていない、同じ内容の予定を探す。
    /// <para>
    /// こちらで入れた予定を相手へ送ったあと、応答を受け取る前に落ちると、
    /// 次の同期で同じものが降ってくる。結び直さないと2件に増える。
    /// </para>
    /// <para>
    /// 探す範囲は、取り込み先のカレンダー（<paramref name="localCalendarId"/>）の中だけ。
    /// </para>
    /// </summary>
    private CalendarEvent? FindUnlinkedMatch(CalendarEvent incoming, string localCalendarId) =>
        events.InRange(incoming.Date, incoming.LastDate)
            .FirstOrDefault(e =>
                e.GoogleEventId is null &&
                // 探すのは、いま取り込んでいる Google のカレンダーに対応する、こちらのカレンダーの中だけ。
                // 範囲を絞らないと、このアプリの中だけのカレンダーの予定や、別のカレンダーに送る
                // つもりの未送信の予定が、たまたま同じ題・日付・開始時刻というだけで吸い込まれ、
                // 相手の内容で上書きされる
                string.Equals(e.CalendarId, localCalendarId, StringComparison.Ordinal) &&
                string.Equals(e.Title, incoming.Title, StringComparison.Ordinal) &&
                e.Date == incoming.Date &&
                e.StartTime == incoming.StartTime);

    // ------------------------------------------------------------------
    // 送る
    // ------------------------------------------------------------------

    private async Task<SyncReport> PushChangesAsync(
        string calendarId, string localCalendarId, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var moved = 0;
        var warnings = new List<string>();
        var now = _clock.GetUtcNow();

        // 送る対象は「内容が変わった」ものだけでなく、「入れ先だけを変えた」ものも含む。
        // 入れ先だけの変更は NeedsPush（内容の比較）では気づけない。
        //
        // 全件を読んでから絞ると、カレンダーが増えるほど無駄が積み重なる
        // （同期はカレンダーごとに回るので、他所の予定まで毎回読むことになる）。
        // DB 側でこのカレンダーの分だけに絞る（EventRepository.ByCalendarId）
        //
        // 「Google 上で見つからない」印が付いたものは送らない（下の catch を見よ）
        var mine = events.ByCalendarId(localCalendarId)
            .Where(e => !e.GoogleMissing)
            .Where(e => EventMapper.NeedsPush(e) || NeedsMove(e, calendarId))
            .ToArray();

        foreach (var value in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (value.GoogleEventId is { Length: > 0 } googleId)
                {
                    var current = value;

                    // 内容の変更を送る先。ふつうはこのカレンダー。移せなかったときは、いま実際に
                    // いる元のカレンダーに送る
                    var patchCalendar = calendarId;
                    var patchLocalCalendar = localCalendarId;

                    if (NeedsMove(current, calendarId))
                    {
                        var origin = current.GoogleCalendarId!;

                        // 移せない予定（繰り返しの1回だけの回・他人が主催する予定・default 以外の種類）は、
                        // 送っても断られるだけ。同期のたびに失敗しないよう、希望を実際の場所へ戻す。
                        // 一緒に直した内容は、そのまま元のカレンダーへ PATCH で送る
                        var blocked = EventMapper.MoveBlockReason(current);
                        string? refused = null;
                        JsonElement? movedElement = null;

                        if (blocked is null)
                        {
                            try
                            {
                                // 消して作り直すと、ゲスト・会議 URL・添付・色が落ちる。move で運ぶ
                                movedElement = await gateway
                                    .MoveAsync(origin, googleId, calendarId, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (GoogleApiException ex) when (!ex.IsMissing && !ex.IsTransient)
                            {
                                // 断られた（主催者でない、など）。出し直しても通らない。
                                // 警告にして希望を戻し、内容の送信は止めない
                                refused = ex.Reason;
                            }
                        }

                        if (movedElement is { } moved1)
                        {
                            // move の応答は「移った」ことだけを表し、本文（タイトルなど）は
                            // 移す前のまま。ここでまるごと FromGoogle に通すと、まだ送れていない
                            // 内容の変更（このあと patch で送るはずのもの）を上書きしてしまうので、
                            // 場所に関する項目だけを取り込む
                            var atDestination = EventMapper.FromGoogle(moved1, localCalendarId, current, now, calendarId);
                            current = current with
                            {
                                GoogleEventId = atDestination.GoogleEventId,
                                GoogleCalendarId = atDestination.GoogleCalendarId,
                                GoogleRaw = atDestination.GoogleRaw,
                                GoogleUpdated = atDestination.GoogleUpdated,
                                UpdatedAt = now,
                            };
                            events.Upsert(current);
                            moved++;

                            // ほかに変えた項目があれば、このあと続けて patch で送る
                        }
                        else
                        {
                            current = current with { CalendarId = origin, UpdatedAt = now };
                            events.Upsert(current);
                            patchCalendar = origin;
                            patchLocalCalendar = origin;

                            warnings.Add(blocked is not null
                                ? $"{blocked}（{current.Title}）。元のカレンダーのままにしました"
                                : $"Google に断られたため、カレンダーを移せませんでした（{current.Title}）: {refused}。" +
                                  "元のカレンダーのままにしました");
                        }
                    }

                    if (EventMapper.NeedsPush(current))
                    {
                        var body = EventMapper.ToGoogle(current);

                        var patched = await gateway
                            .PatchAsync(patchCalendar, googleId, body, cancellationToken)
                            .ConfigureAwait(false);

                        // 応答をそのまま控える。次の同期で「変わった」と誤判定しないため
                        events.Upsert(EventMapper.FromGoogle(patched, patchLocalCalendar, current, now, patchCalendar));
                        updated++;
                    }
                }
                else
                {
                    var body = EventMapper.ToGoogle(value);

                    var inserted = await gateway
                        .InsertAsync(calendarId, body, cancellationToken)
                        .ConfigureAwait(false);

                    events.Upsert(EventMapper.FromGoogle(inserted, localCalendarId, value, now, calendarId));
                    created++;
                }
            }
            catch (GoogleApiException ex) when (ex.IsMissing && value.GoogleEventId is not null)
            {
                // 相手が「無い」と言った。<b>黙って作り直さない。</b>結び付きを外して次の同期で
                // insert すると、別の場所へ移っていただけのものが二重になり、ゲスト・会議 URL・
                // 添付の無い写しが増える。印だけを付けて、以後は送らない。
                //
                // 取り込みで見つかれば印は外れる。使う人が編集画面で「Google に新しく作り直す」を
                // 選んだときだけ、結び付きを外して新規として送る
                // （途中の move で行が書き換わっていることがあるので、読み直してから印を付ける）
                events.Upsert((events.Find(value.Id) ?? value) with { GoogleMissing = true });

                warnings.Add(
                    $"Google 上で見つからないため送りません（{value.Title}）。" +
                    "編集画面から「Google に新しく作り直す」を選べます");
            }
            catch (GoogleApiException ex) when (ex.IsTransient)
            {
                warnings.Add($"送れませんでした（{value.Title}）: {ex.Reason}");
            }
            catch (GoogleApiException ex)
            {
                // 1件が受け付けられないだけで、そのカレンダーの同期全体を止めない。
                // 実機で、送れない予定が1件あるだけで他の予定まで一切動かなくなった。
                // どの予定が、なぜ断られたのかを残して次へ進む
                warnings.Add(ex.Description is { Length: > 0 } detail
                    ? $"送れませんでした（{value.Title}）: {ex.Reason} — {detail}"
                    : $"送れませんでした（{value.Title}）: {ex.Reason}");
            }
        }

        return new SyncReport
        {
            CreatedRemote = created,
            UpdatedRemote = updated,
            Moved = moved,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// このカレンダー（<paramref name="calendarId"/>）へ、<c>events.move</c> で
    /// 運ぶ必要があるか。
    /// <para>
    /// Google 側で実際にいる場所（<see cref="CalendarEvent.GoogleCalendarId"/>）と、
    /// こちらの希望（<see cref="CalendarEvent.CalendarId"/> ＝いま送ろうとしている先）が
    /// 食い違っているときだけ true。まだ一度も Google の応答を受けていない予定
    /// （<c>GoogleCalendarId</c> が null）は対象にしない。
    /// </para>
    /// </summary>
    private static bool NeedsMove(CalendarEvent value, string calendarId) =>
        value.GoogleEventId is { Length: > 0 } &&
        value.GoogleCalendarId is { Length: > 0 } origin &&
        !string.Equals(origin, calendarId, StringComparison.Ordinal);
}
