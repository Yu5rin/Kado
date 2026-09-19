using System.Text.Json;
using System.Text.Json.Nodes;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Sync;

/// <summary>
/// 予定について、Google 側へ出入りする口。
/// <para>実物は <see cref="GoogleCalendarApi"/>。試験では差し替える。</para>
/// </summary>
public interface IEventGateway
{
    Task<GooglePage> ListAsync(
        string calendarId, string? syncToken, string? pageToken, CancellationToken cancellationToken);

    Task<JsonElement> InsertAsync(string calendarId, JsonObject body, CancellationToken cancellationToken);

    Task<JsonElement> PatchAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken);

    Task DeleteAsync(string calendarId, string eventId, CancellationToken cancellationToken);
}

/// <summary><see cref="GoogleCalendarApi"/> を上の口に合わせる。</summary>
public sealed class CalendarApiGateway(GoogleCalendarApi api, DateTimeOffset? from = null) : IEventGateway
{
    public Task<GooglePage> ListAsync(
        string calendarId, string? syncToken, string? pageToken, CancellationToken cancellationToken) =>
        api.ListEventsAsync(calendarId, syncToken, pageToken, from, cancellationToken);

    public Task<JsonElement> InsertAsync(
        string calendarId, JsonObject body, CancellationToken cancellationToken) =>
        api.InsertEventAsync(calendarId, body, cancellationToken);

    public Task<JsonElement> PatchAsync(
        string calendarId, string eventId, JsonObject body, CancellationToken cancellationToken) =>
        api.PatchEventAsync(calendarId, eventId, body, cancellationToken);

    public Task DeleteAsync(string calendarId, string eventId, CancellationToken cancellationToken) =>
        api.DeleteEventAsync(calendarId, eventId, cancellationToken);
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

    /// <summary>このカレンダーの差分の印を覚えておくキー。</summary>
    private static string TokenKey(string calendarId) => $"calendar:{calendarId}:syncToken";

    /// <summary>
    /// 同期する。
    /// </summary>
    /// <param name="calendarId">Google 側のカレンダー ID。</param>
    /// <param name="localCalendarId">こちらのカレンダー ID。取り込んだ予定の所属になる。</param>
    public async Task<SyncReport> SyncAsync(
        string calendarId, string localCalendarId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);

        var report = await PushDeletionsAsync(calendarId, cancellationToken).ConfigureAwait(false);
        report += await PullAsync(calendarId, localCalendarId, cancellationToken).ConfigureAwait(false);
        report += await PushChangesAsync(calendarId, localCalendarId, cancellationToken).ConfigureAwait(false);

        return report;
    }

    // ------------------------------------------------------------------
    // 削除を伝える。取り込みより先に行う。あとだと消したものが復活する
    // ------------------------------------------------------------------

    private async Task<SyncReport> PushDeletionsAsync(string calendarId, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var warnings = new List<string>();

        foreach (var tombstone in tombstones.Pending(TombstoneRepository.EventKind))
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
                // すでに無い。望む状態なので記録だけ片付ける
                tombstones.Clear(tombstone.Id, TombstoneRepository.EventKind);
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
        var now = _clock.GetUtcNow();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GoogleJson.Text(item, "id") is not { } googleId) continue;

            var existing = events.FindByGoogleId(googleId);

            if (EventMapper.IsCancelled(item))
            {
                // 相手が消した。こちらでも消すが、tombstone は要らない。
                // 相手はもう知っている。残すと次の同期で消しに行ってしまう
                if (existing is not null && events.Delete(existing.Id)) deleted++;
                continue;
            }

            // こちらで消したものは復活させない
            if (tombstones.ContainsGoogleId(googleId, TombstoneRepository.EventKind)) continue;

            // 相手が変わっていなければ触らない。全部取り直したときに、まだ送れていない
            // こちらの変更を押し潰してしまう。相手が変わっていれば、方針どおり相手を採る
            if (existing is not null &&
                GoogleJson.SameContent(existing.GoogleRaw, GoogleJson.Normalize(item)))
            {
                continue;
            }

            var mapped = EventMapper.FromGoogle(item, localCalendarId, existing, now);

            if (existing is null)
            {
                // 同じ予定が2件に増えないよう、結び付いていないものを探して引き受ける
                if (FindUnlinkedMatch(mapped) is { } orphan)
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
            FullResync = fullResync,
        };
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
    /// まだ結び付いていない、同じ内容の予定を探す。
    /// <para>
    /// こちらで入れた予定を相手へ送ったあと、応答を受け取る前に落ちると、
    /// 次の同期で同じものが降ってくる。結び直さないと2件に増える。
    /// </para>
    /// </summary>
    private CalendarEvent? FindUnlinkedMatch(CalendarEvent incoming) =>
        events.InRange(incoming.Date, incoming.LastDate)
            .FirstOrDefault(e =>
                e.GoogleEventId is null &&
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
        var relinked = 0;
        var warnings = new List<string>();
        var now = _clock.GetUtcNow();

        var mine = events.All()
            .Where(e => string.Equals(e.CalendarId, localCalendarId, StringComparison.Ordinal))
            .Where(EventMapper.NeedsPush)
            .ToArray();

        foreach (var value in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var body = EventMapper.ToGoogle(value);

                if (value.GoogleEventId is { Length: > 0 } googleId)
                {
                    var patched = await gateway
                        .PatchAsync(calendarId, googleId, body, cancellationToken)
                        .ConfigureAwait(false);

                    // 応答をそのまま控える。次の同期で「変わった」と誤判定しないため
                    events.Upsert(EventMapper.FromGoogle(patched, localCalendarId, value, now));
                    updated++;
                }
                else
                {
                    var inserted = await gateway
                        .InsertAsync(calendarId, body, cancellationToken)
                        .ConfigureAwait(false);

                    events.Upsert(EventMapper.FromGoogle(inserted, localCalendarId, value, now));
                    created++;
                }
            }
            catch (GoogleApiException ex) when (ex.IsMissing && value.GoogleEventId is not null)
            {
                // 相手から消えていた。結びを外して、次の同期で作り直させる
                events.Upsert(value with { GoogleEventId = null, GoogleRaw = null, UpdatedAt = now });
                relinked++;
            }
            catch (GoogleApiException ex) when (ex.IsTransient)
            {
                warnings.Add($"送れませんでした（{value.Title}）: {ex.Reason}");
            }
        }

        return new SyncReport
        {
            CreatedRemote = created,
            UpdatedRemote = updated,
            Relinked = relinked,
            Warnings = warnings,
        };
    }
}
