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
}

/// <summary><see cref="GoogleCalendarApi"/> を上の口に合わせる。</summary>
public sealed class CalendarApiGateway(GoogleCalendarApi api, DateTimeOffset? from = null) : IEventGateway
{
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
            catch (GoogleApiException ex) when (ex.IsMissing)
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
            catch (GoogleApiException ex) when (ex.IsTransient)
            {
                // 次の同期で出し直す。記録は残す
                warnings.Add($"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason}");
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

        List<JsonElement> items;
        string? nextToken;

        try
        {
            (items, nextToken) = await ReadAllPagesAsync(calendarId, token, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (ex.NeedsFullResync)
        {
            // 差分では追いつけない。印を捨てて全部取り直す（要件書 6.3）
            fullResync = true;
            settings.SetSyncState(TokenKey(calendarId), string.Empty);

            (items, nextToken) = await ReadAllPagesAsync(calendarId, null, cancellationToken).ConfigureAwait(false);
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

    private async Task<(List<JsonElement> Items, string? NextSyncToken)> ReadAllPagesAsync(
        string calendarId, string? syncToken, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? pageToken = null;
        string? nextSyncToken = null;

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
            if (pageToken is not { Length: > 0 }) break;
        }

        return (items, nextSyncToken);
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

                    if (NeedsMove(current, calendarId))
                    {
                        var origin = current.GoogleCalendarId!;

                        if (EventMapper.IsRecurringInstance(current))
                        {
                            // 繰り返しのうち1回だけの回は、Google でもカレンダーを移せない。
                            // 編集画面で止めているはずだが、ここまで来た分は壊さない側に倒し、
                            // こちらの希望（入れ先）を実際の場所へ戻す
                            events.Upsert(current with { CalendarId = origin, UpdatedAt = now });
                            warnings.Add(
                                $"繰り返しの1回だけの予定はカレンダーを移せません（{current.Title}）。" +
                                "元のカレンダーのままにしました");
                            continue;
                        }

                        // 消して作り直すと、ゲスト・会議 URL・添付・色が落ちる。move で運ぶ
                        var movedElement = await gateway
                            .MoveAsync(origin, googleId, calendarId, cancellationToken)
                            .ConfigureAwait(false);

                        // move の応答は「移った」ことだけを表し、本文（タイトルなど）は
                        // 移す前のまま。ここでまるごと FromGoogle に通すと、まだ送れていない
                        // 内容の変更（このあと patch で送るはずのもの）を上書きしてしまうので、
                        // 場所に関する項目だけを取り込む
                        var atDestination = EventMapper.FromGoogle(movedElement, localCalendarId, current, now, calendarId);
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

                    if (EventMapper.NeedsPush(current))
                    {
                        var body = EventMapper.ToGoogle(current);

                        var patched = await gateway
                            .PatchAsync(calendarId, googleId, body, cancellationToken)
                            .ConfigureAwait(false);

                        // 応答をそのまま控える。次の同期で「変わった」と誤判定しないため
                        events.Upsert(EventMapper.FromGoogle(patched, localCalendarId, current, now, calendarId));
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
