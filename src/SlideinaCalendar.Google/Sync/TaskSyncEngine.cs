using System.Text.Json;
using System.Text.Json.Nodes;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Google.Mapping;

namespace SlideinaCalendar.Google.Sync;

/// <summary>タスクについて、Google 側へ出入りする口。</summary>
public interface ITaskGateway
{
    Task<GooglePage> ListAsync(
        string taskListId, DateTimeOffset? updatedSince, string? pageToken, CancellationToken cancellationToken);

    Task<JsonElement> InsertAsync(string taskListId, JsonObject body, CancellationToken cancellationToken);

    Task<JsonElement> PatchAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken);

    Task DeleteAsync(string taskListId, string taskId, CancellationToken cancellationToken);
}

/// <summary><see cref="GoogleTasksApi"/> を上の口に合わせる。</summary>
public sealed class TasksApiGateway(GoogleTasksApi api) : ITaskGateway
{
    public Task<GooglePage> ListAsync(
        string taskListId, DateTimeOffset? updatedSince, string? pageToken, CancellationToken cancellationToken) =>
        api.ListTasksAsync(taskListId, updatedSince, pageToken, cancellationToken);

    public Task<JsonElement> InsertAsync(
        string taskListId, JsonObject body, CancellationToken cancellationToken) =>
        api.InsertTaskAsync(taskListId, body, cancellationToken);

    public Task<JsonElement> PatchAsync(
        string taskListId, string taskId, JsonObject body, CancellationToken cancellationToken) =>
        api.PatchTaskAsync(taskListId, taskId, body, cancellationToken);

    public Task DeleteAsync(string taskListId, string taskId, CancellationToken cancellationToken) =>
        api.DeleteTaskAsync(taskListId, taskId, cancellationToken);
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

    /// <summary>前回いつ取りに行ったかを覚えておくキー。</summary>
    private static string SinceKey(string taskListId) => $"tasks:{taskListId}:updatedMin";

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

        foreach (var tombstone in tombstones.Pending(TombstoneRepository.TaskKind))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await gateway.DeleteAsync(taskListId, tombstone.GoogleId!, cancellationToken).ConfigureAwait(false);

                tombstones.Clear(tombstone.Id, TombstoneRepository.TaskKind);
                deleted++;
            }
            catch (GoogleApiException ex) when (ex.IsMissing)
            {
                tombstones.Clear(tombstone.Id, TombstoneRepository.TaskKind);
            }
            catch (GoogleApiException ex) when (ex.IsTransient)
            {
                warnings.Add($"削除を伝えられませんでした（{tombstone.Id}）: {ex.Reason}");
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

        var items = await ReadAllPagesAsync(taskListId, since, cancellationToken).ConfigureAwait(false);

        var created = 0;
        var updated = 0;
        var deleted = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GoogleJson.Text(item, "id") is not { } googleId) continue;

            var existing = tasks.FindByGoogleId(googleId);

            if (TaskMapper.IsDeleted(item))
            {
                // 相手が消した。tombstone は残さない。相手はもう知っている
                if (existing is not null && tasks.Delete(existing.Id)) deleted++;
                continue;
            }

            if (tombstones.ContainsGoogleId(googleId, TombstoneRepository.TaskKind)) continue;

            // 相手が変わっていなければ触らない。送れていないこちらの変更を潰さないため
            if (existing is not null &&
                GoogleJson.SameContent(existing.GoogleRaw, GoogleJson.Normalize(item)))
            {
                continue;
            }

            var mapped = TaskMapper.FromGoogle(item, taskListId, localListId, existing, startedAt);

            if (existing is null)
            {
                if (FindUnlinkedMatch(mapped) is { } orphan)
                {
                    tasks.Upsert(mapped with { Id = orphan.Id });
                    updated++;
                    continue;
                }

                tasks.Upsert(mapped);
                created++;
            }
            else
            {
                tasks.Upsert(mapped);
                updated++;
            }
        }

        settings.SetSyncState(SinceKey(taskListId), startedAt.ToString("O"));

        return new SyncReport { CreatedLocal = created, UpdatedLocal = updated, DeletedLocal = deleted };
    }

    private DateTimeOffset? ReadSince(string taskListId) =>
        settings.GetSyncState(SinceKey(taskListId)) is { Length: > 0 } text &&
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private async Task<List<JsonElement>> ReadAllPagesAsync(
        string taskListId, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? pageToken = null;

        for (var page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await gateway
                .ListAsync(taskListId, since, pageToken, cancellationToken)
                .ConfigureAwait(false);

            items.AddRange(result.Items);

            pageToken = result.NextPageToken;
            if (pageToken is not { Length: > 0 }) break;
        }

        return items;
    }

    /// <summary>まだ結び付いていない、同じ内容のタスクを探す。</summary>
    private TaskItem? FindUnlinkedMatch(TaskItem incoming) =>
        tasks.All()
            .FirstOrDefault(t =>
                t.GoogleTaskId is null &&
                string.Equals(t.Title, incoming.Title, StringComparison.Ordinal) &&
                t.Due == incoming.Due);

    private async Task<SyncReport> PushChangesAsync(
        string taskListId, string localListId, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var relinked = 0;
        var warnings = new List<string>();
        var now = _clock.GetUtcNow();

        var mine = tasks.All()
            .Where(t => string.Equals(t.TaskListId, localListId, StringComparison.Ordinal))
            .Where(TaskMapper.NeedsPush)
            .ToArray();

        foreach (var value in mine)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var body = TaskMapper.ToGoogle(value);

                if (value.GoogleTaskId is { Length: > 0 } googleId)
                {
                    var patched = await gateway
                        .PatchAsync(taskListId, googleId, body, cancellationToken)
                        .ConfigureAwait(false);

                    tasks.Upsert(TaskMapper.FromGoogle(patched, taskListId, localListId, value, now));
                    updated++;
                }
                else
                {
                    var inserted = await gateway
                        .InsertAsync(taskListId, body, cancellationToken)
                        .ConfigureAwait(false);

                    tasks.Upsert(TaskMapper.FromGoogle(inserted, taskListId, localListId, value, now));
                    created++;
                }
            }
            catch (GoogleApiException ex) when (ex.IsMissing && value.GoogleTaskId is not null)
            {
                tasks.Upsert(value with { GoogleTaskId = null, GoogleRaw = null, UpdatedAt = now });
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
