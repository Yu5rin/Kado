using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;

namespace Kado.Google.Sync;

/// <summary>タスクについて、Google 側へ出入りする口。</summary>
public interface ITaskGateway
{
    Task<GooglePage> ListAsync(
        string taskListId, DateTimeOffset? updatedSince, string? pageToken, CancellationToken cancellationToken);

    /// <summary>タスクを1件取る。無ければ 404。移す指示を出していたタスクが移し先にあるかを確かめる。</summary>
    Task<JsonElement> GetAsync(string taskListId, string taskId, CancellationToken cancellationToken);

    Task<JsonElement> InsertAsync(string taskListId, JsonObject body, CancellationToken cancellationToken);

    Task<JsonElement> PatchAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken);

    Task DeleteAsync(string taskListId, string taskId, CancellationToken cancellationToken);

    /// <summary>タスクを別のリストへ移す。消して作り直すより並び順や親子関係を保てる。</summary>
    Task<JsonElement> MoveAsync(
        string sourceTaskListId, string taskId, string destinationTaskListId,
        CancellationToken cancellationToken);
}

/// <summary><see cref="GoogleTasksApi"/> を上の口に合わせる。</summary>
public sealed class TasksApiGateway(GoogleTasksApi api) : ITaskGateway
{
    public Task<GooglePage> ListAsync(
        string taskListId, DateTimeOffset? updatedSince, string? pageToken, CancellationToken cancellationToken) =>
        api.ListTasksAsync(taskListId, updatedSince, pageToken, cancellationToken);

    public Task<JsonElement> GetAsync(
        string taskListId, string taskId, CancellationToken cancellationToken) =>
        api.GetTaskAsync(taskListId, taskId, cancellationToken);

    public Task<JsonElement> InsertAsync(
        string taskListId, JsonObject body, CancellationToken cancellationToken) =>
        api.InsertTaskAsync(taskListId, body, cancellationToken);

    public Task<JsonElement> PatchAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken) =>
        api.PatchTaskAsync(taskListId, taskId, body, cancellationToken);

    public Task DeleteAsync(string taskListId, string taskId, CancellationToken cancellationToken) =>
        api.DeleteTaskAsync(taskListId, taskId, cancellationToken);

    public Task<JsonElement> MoveAsync(
        string sourceTaskListId, string taskId, string destinationTaskListId,
        CancellationToken cancellationToken) =>
        api.MoveTaskAsync(sourceTaskListId, taskId, destinationTaskListId, cancellationToken);
}

/// <summary>
/// タスクの同期。
/// <para>
/// 予定と同じ順序で回すが、差分の取り方が違う。Tasks には <c>syncToken</c> が無いので、
/// <b>前回いつ取りに行ったか</b>を覚えて <c>updatedMin</c> に渡す。
/// </para>
/// <para>
/// 消えたタスクは <c>deleted: true</c> で降ってくる。予定の <c>cancelled</c> と違い、
/// <c>hidden</c>（完了して一覧から隠れただけ）とは別物なので混ぜない。
/// </para>
/// </summary>
public sealed class TaskSyncEngine(
    TaskRepository tasks,
    TombstoneRepository tombstones,
    SettingsRepository settings,
    ITaskGateway gateway,
    TimeProvider? clock = null)
{
    private const int MaxPages = 50;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// 前回いつ取りに行ったかを覚えておくキー。
    /// <para>印を捨てて全件を取り直させたいとき（一覧から外れて戻った、など）に、呼び出し側も使う。</para>
    /// </summary>
    public static string SinceKey(string taskListId) => $"tasks:{taskListId}:updatedMin";

    /// <summary>同期する。</summary>
    /// <param name="taskListId">Google 側のタスクリスト ID。</param>
    /// <param name="localListId">こちらのタスクリスト ID。</param>
    public async Task<SyncReport> SyncAsync(
        string taskListId, string localListId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskListId);

        var report = await PushDeletionsAsync(taskListId, cancellationToken).ConfigureAwait(false);
        report += await PullAsync(taskListId, localListId, cancellationToken).ConfigureAwait(false);
        report += await PushChangesAsync(taskListId, localListId, cancellationToken).ConfigureAwait(false);

        return report;
    }

    // ------------------------------------------------------------------

    private async Task<SyncReport> PushDeletionsAsync(string taskListId, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var warnings = new List<string>();

        // このタスクリストのものだけ。予定のときと同じ理由（EventSyncEngine を見よ）
        foreach (var tombstone in tombstones.Pending(TombstoneRepository.TaskKind, taskListId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await gateway.DeleteAsync(taskListId, tombstone.GoogleId!, cancellationToken).ConfigureAwait(false);

                tombstones.Clear(tombstone.Id, TombstoneRepository.TaskKind);
                deleted++;
            }
            catch (GoogleApiException ex) when (ex.IsMissing || ex.Status == HttpStatusCode.Gone)
            {
                // 持ち主が分からない古い記録は、次のリストが拾えるよう残す
                if (tombstone.SourceId is { Length: > 0 })
                {
                    tombstones.Clear(tombstone.Id, TombstoneRepository.TaskKind);
                }
            }
            catch (GoogleApiException ex) when (ex.IsPermissionDenied)
            {
                // 権限が無い。出し直しても通らないので、記録を捨てて知らせる（EventSyncEngine と同じ）
                tombstones.Clear(tombstone.Id, TombstoneRepository.TaskKind);
                warnings.Add(
                    $"権限が無いため、Google 側のタスクを削除できません（{tombstone.Id}）: {ex.Reason}。" +
                    "手元からは消えていますが、Google には残っています");
            }
            catch (GoogleApiException ex)
            {
                // 呼びすぎ・サーバー側の不調と、その他の 4xx。1件の削除が断られただけで、
                // このリストの取り込みも送信も止めない。記録は残し、次の同期でやり直す
                warnings.Add(ex.Description is { Length: > 0 } detail
                    ? $"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason} — {detail}"
                    : $"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason}");
            }
        }

        return new SyncReport { DeletedRemote = deleted, Warnings = warnings };
    }

    private async Task<SyncReport> PullAsync(
        string taskListId, string localListId, CancellationToken cancellationToken)
    {
        var since = ReadSince(taskListId);

        // 取りに行く前の時刻を控える。読んでいる最中の変更を次回に拾えるようにする
        var startedAt = _clock.GetUtcNow();

        var (items, complete) = await ReadAllPagesAsync(taskListId, since, cancellationToken).ConfigureAwait(false);

        var created = 0;
        var updated = 0;
        var deleted = 0;
        var relinked = 0;
        var now = _clock.GetUtcNow();
        var overwritten = new List<string>();
        var warnings = new List<string>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GoogleJson.Text(item, "id") is not { } googleId) continue;

            var existing = tasks.FindByGoogleId(googleId);

            if (TaskMapper.IsDeleted(item))
            {
                // 相手が消した。tombstone は残さない。相手はもう知っている。
                //
                // ただし、このリストからの deleted は「利用者が削除した」だけとは限らない。
                // こちらで入れ先を別のリストへ変え、すでに tasks.move で移したあとにも、
                // 元のリスト（このリスト）側では同じ知らせが返る（ShouldDeleteOnCancel を見よ）
                if (ShouldDeleteOnCancel(existing, taskListId) &&
                    await DeleteLocalAsync(existing!, now, warnings, cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }

                continue;
            }

            if (tombstones.ContainsGoogleId(googleId, TombstoneRepository.TaskKind)) continue;

            // 相手が変わっていなければ触らない。送れていないこちらの変更を潰さないため
            if (existing is not null &&
                GoogleJson.SameContent(existing.GoogleRaw, GoogleJson.Normalize(item)))
            {
                // 「Google 上で見つからない」印が付いていたものが、また見つかった（別のリストに
                // いた、など）。内容は触らず、印だけを外して結び直す
                if (existing.GoogleMissing)
                {
                    tasks.Upsert(existing with { GoogleMissing = false, GoogleTaskListId = taskListId });
                    relinked++;
                }

                continue;
            }

            // Google を採る方針は変えないが、こちらにまだ送れていない編集があるなら
            // それを黙って捨てることになる。気づけるよう警告に残す（EventSyncEngine と同じ）
            if (existing is not null && TaskMapper.NeedsPush(existing))
            {
                overwritten.Add(existing.Title is { Length: > 0 } title ? title : "(無題)");
            }

            var mapped = TaskMapper.FromGoogle(item, taskListId, localListId, existing, startedAt);

            if (existing is null)
            {
                if (FindUnlinkedMatch(mapped, localListId) is { } orphan)
                {
                    // orphan を existing として渡し直す。作成日時・並び順はローカルにしか
                    // 無い項目なので、結び付けただけで消してしまわないようにする
                    tasks.Upsert(TaskMapper.FromGoogle(item, taskListId, localListId, orphan, startedAt));
                    updated++;
                    continue;
                }

                // 新規は、この期限日（期限なしなら期限なしどうし）の末尾に置く
                tasks.Upsert(mapped with { SortOrder = tasks.NextSortOrder(mapped.Due) });
                created++;
            }
            else
            {
                tasks.Upsert(mapped);
                updated++;
            }
        }

        // 前回の時刻が無い（初回・繋ぎ直し）ときは全件を取っている。消えたタスクは、
        // 時間が経つと deleted: true でも降ってこない。一覧に無いことで見つけて片付ける。
        // 一覧を最後まで読めたときだけ行う
        if (since is null && complete)
        {
            deleted += RemoveVanished(taskListId, localListId, items, warnings);
        }

        settings.SetSyncState(SinceKey(taskListId), startedAt.ToString("O"));

        return new SyncReport
        {
            CreatedLocal = created,
            UpdatedLocal = updated,
            DeletedLocal = deleted,
            Relinked = relinked,
            Warnings = [.. warnings, .. SummarizeOverwritten(overwritten)],
        };
    }

    /// <summary>
    /// 相手の削除（deleted）を受けて、こちらの行を消す。確かめることは
    /// <see cref="EventSyncEngine"/> の同名の処理と同じ（理由もそちらを見よ）。
    /// </summary>
    /// <returns>消したら true。</returns>
    private async Task<bool> DeleteLocalAsync(
        TaskItem existing, DateTimeOffset now, List<string> warnings, CancellationToken cancellationToken)
    {
        if (await IsAlreadyMovedAsync(existing, cancellationToken).ConfigureAwait(false))
        {
            tasks.Upsert(existing with { GoogleTaskListId = existing.TaskListId, UpdatedAt = now });
            return false;
        }

        if (TaskMapper.NeedsPush(existing))
        {
            warnings.Add(
                "Google 側で削除されたため、こちらの未送信の変更を捨てました：" +
                (existing.Title is { Length: > 0 } title ? title : "(無題)"));
        }

        return tasks.Delete(existing.Id);
    }

    /// <summary>移す指示を出していたタスクが、移し先に本当にあるか。</summary>
    private async Task<bool> IsAlreadyMovedAsync(TaskItem existing, CancellationToken cancellationToken)
    {
        if (existing.GoogleTaskId is not { Length: > 0 } googleId) return false;
        if (existing.GoogleTaskListId is not { Length: > 0 } origin) return false;
        if (existing.TaskListId is not { Length: > 0 } destination) return false;
        if (string.Equals(origin, destination, StringComparison.Ordinal)) return false;

        try
        {
            var found = await gateway.GetAsync(destination, googleId, cancellationToken).ConfigureAwait(false);

            // 消されたタスクは 404 ではなく deleted: true で返ることがある。それは「ある」ではない
            return !TaskMapper.IsDeleted(found);
        }
        catch (GoogleApiException ex) when (!ex.IsTransient)
        {
            return false;
        }
    }

    /// <summary>
    /// 未送信の編集を捨てて Google 側を採ったタスクを、警告文にまとめる。
    /// <para>まとめ方は EventSyncEngine と揃える。</para>
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

    private DateTimeOffset? ReadSince(string taskListId) =>
        settings.GetSyncState(SinceKey(taskListId)) is { Length: > 0 } text &&
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private async Task<(List<JsonElement> Items, bool Complete)> ReadAllPagesAsync(
        string taskListId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? pageToken = null;
        var complete = false;

        for (var page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await gateway
                .ListAsync(taskListId, since, pageToken, cancellationToken)
                .ConfigureAwait(false);

            items.AddRange(result.Items);

            pageToken = result.NextPageToken;

            if (pageToken is not { Length: > 0 })
            {
                complete = true;
                break;
            }
        }

        return (items, complete);
    }

    /// <summary>
    /// 全件を取った一覧に無い、Google と結び付いたタスクを片付ける。
    /// <para>考え方は <c>EventSyncEngine.RemoveVanished</c> と同じ（理由もそちらを見よ）。</para>
    /// </summary>
    /// <returns>手元から消した件数。</returns>
    private int RemoveVanished(
        string taskListId, string localListId, List<JsonElement> listed, List<string> warnings)
    {
        var listedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in listed)
        {
            if (GoogleJson.Text(item, "id") is { } id) listedIds.Add(id);
        }

        var removed = 0;
        var kept = new List<string>();

        foreach (var value in tasks.All())
        {
            if (value.GoogleTaskId is not { Length: > 0 } googleId) continue;
            if (listedIds.Contains(googleId) || value.GoogleMissing) continue;

            var lives = value.GoogleTaskListId is { Length: > 0 } actual
                ? string.Equals(actual, taskListId, StringComparison.Ordinal)
                : string.Equals(value.TaskListId, localListId, StringComparison.Ordinal);
            if (!lives) continue;

            if (TaskMapper.NeedsPush(value) || NeedsMove(value, taskListId))
            {
                tasks.Upsert(value with { GoogleMissing = true });
                kept.Add(value.Title is { Length: > 0 } title ? title : "(無題)");
                continue;
            }

            if (tasks.Delete(value.Id)) removed++;
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
    /// このリストからの <c>deleted</c> を、削除として受け取ってよいか。
    /// <para>EventSyncEngine.ShouldDeleteOnCancel と同じ考え方。理由もそちらを見よ。</para>
    /// </summary>
    private static bool ShouldDeleteOnCancel(TaskItem? existing, string taskListId) =>
        existing is not null &&
        (existing.GoogleTaskListId is not { Length: > 0 } known ||
         string.Equals(known, taskListId, StringComparison.Ordinal));

    /// <summary>
    /// まだ結び付いていない、同じ内容のタスクを探す。
    /// <para>
    /// 探す範囲は、取り込み先のリスト（<paramref name="localListId"/>）の中だけ。範囲を絞らないと、
    /// このアプリの中だけのリストや別のリストのタスクが、同じ題・期限というだけで吸い込まれる。
    /// </para>
    /// </summary>
    private TaskItem? FindUnlinkedMatch(TaskItem incoming, string localListId) =>
        tasks.All()
            .FirstOrDefault(t =>
                t.GoogleTaskId is null &&
                string.Equals(t.TaskListId, localListId, StringComparison.Ordinal) &&
                string.Equals(t.Title, incoming.Title, StringComparison.Ordinal) &&
                t.Due == incoming.Due);

    private async Task<SyncReport> PushChangesAsync(
        string taskListId, string localListId, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var moved = 0;
        var warnings = new List<string>();
        var now = _clock.GetUtcNow();

        // 送る対象は「内容が変わった」ものだけでなく、「入れ先だけを変えた」ものも含む
        //
        // 「Google 上で見つからない」印が付いたものは送らない（下の catch を見よ）
        var mine = tasks.All()
            .Where(t => string.Equals(t.TaskListId, localListId, StringComparison.Ordinal))
            .Where(t => !t.GoogleMissing)
            .Where(t => TaskMapper.NeedsPush(t) || NeedsMove(t, taskListId))
            .ToArray();

        foreach (var value in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (value.GoogleTaskId is { Length: > 0 } googleId)
                {
                    var current = value;

                    // 内容の変更を送る先。移せなかったときは、いま実際にいる元のリストに送る
                    var patchList = taskListId;
                    var patchLocalList = localListId;

                    if (NeedsMove(current, taskListId))
                    {
                        var origin = current.GoogleTaskListId!;

                        // 親子は Google が持つ。子と、子を持つ親は別のリストへ移せない。
                        // 送っても断られる・親子が壊れるので、希望を実際の場所へ戻し、
                        // 一緒に直した内容は元のリストへ PATCH で送る
                        var blocked = TaskMapper.MoveBlockReason(current, TaskMapper.HasChildren(current, tasks.All()));
                        string? refused = null;
                        JsonElement? movedElement = null;

                        if (blocked is null)
                        {
                            try
                            {
                                // 消して作り直すより、tasks.move のほうが並び順や親子関係を保てる
                                movedElement = await gateway
                                    .MoveAsync(origin, googleId, taskListId, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (GoogleApiException ex) when (!ex.IsMissing && !ex.IsTransient)
                            {
                                refused = ex.Reason;
                            }
                        }

                        if (movedElement is { } moved1)
                        {
                            // move の応答は本文（タイトルなど）が移す前のまま。まるごと
                            // FromGoogle に通すと、まだ送れていない内容の変更を上書きしてしまう
                            // ので、場所に関する項目だけを取り込む（EventSyncEngine と同じ理由）
                            var atDestination = TaskMapper.FromGoogle(moved1, taskListId, localListId, current, now);
                            current = current with
                            {
                                GoogleTaskId = atDestination.GoogleTaskId,
                                GoogleTaskListId = atDestination.GoogleTaskListId,
                                GoogleRaw = atDestination.GoogleRaw,
                                GoogleUpdated = atDestination.GoogleUpdated,
                                UpdatedAt = now,
                            };
                            tasks.Upsert(current);
                            moved++;
                        }
                        else
                        {
                            current = current with { TaskListId = origin, UpdatedAt = now };
                            tasks.Upsert(current);
                            patchList = origin;
                            patchLocalList = origin;

                            warnings.Add(blocked is not null
                                ? $"{blocked}（{current.Title}）。元のリストのままにしました"
                                : $"Google に断られたため、リストを移せませんでした（{current.Title}）: {refused}。" +
                                  "元のリストのままにしました");
                        }
                    }

                    if (TaskMapper.NeedsPush(current))
                    {
                        var body = TaskMapper.ToGoogle(current);

                        var patched = await gateway
                            .PatchAsync(patchList, googleId, body, cancellationToken)
                            .ConfigureAwait(false);

                        tasks.Upsert(TaskMapper.FromGoogle(patched, patchList, patchLocalList, current, now));
                        updated++;
                    }
                }
                else
                {
                    var body = TaskMapper.ToGoogle(value);

                    var inserted = await gateway
                        .InsertAsync(taskListId, body, cancellationToken)
                        .ConfigureAwait(false);

                    tasks.Upsert(TaskMapper.FromGoogle(inserted, taskListId, localListId, value, now));
                    created++;
                }
            }
            catch (GoogleApiException ex) when (ex.IsMissing && value.GoogleTaskId is not null)
            {
                // 相手が「無い」と言った。黙って作り直さない（EventSyncEngine と同じ理由）。
                // 印だけを付けて、以後は送らない。取り込みで見つかれば外れ、使う人が
                // 「Google に新しく作り直す」を選んだときだけ新規として送る
                tasks.Upsert((tasks.Find(value.Id) ?? value) with { GoogleMissing = true });

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
                // 1件が受け付けられないだけで、そのリストの同期全体を止めない
                // （EventSyncEngine と同じ理由。実機で、送れないタスクが1件あるだけで
                // 他のタスクまで一切動かなくなった）。どのタスクが、なぜ断られたのかを
                // 残して次へ進む
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
    /// このリスト（<paramref name="taskListId"/>）へ、<c>tasks.move</c> で運ぶ必要があるか。
    /// <para>EventSyncEngine.NeedsMove と同じ考え方。</para>
    /// </summary>
    private static bool NeedsMove(TaskItem value, string taskListId) =>
        value.GoogleTaskId is { Length: > 0 } &&
        value.GoogleTaskListId is { Length: > 0 } origin &&
        !string.Equals(origin, taskListId, StringComparison.Ordinal);
}
