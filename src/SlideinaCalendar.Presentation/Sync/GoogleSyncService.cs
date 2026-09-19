using System.Text.Json;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Google.Mapping;
using SlideinaCalendar.Google.Sync;

namespace SlideinaCalendar.Presentation.Sync;

/// <summary>
/// 画面から同期を回す口。
/// <para>
/// カレンダー一覧を取り込んでから、カレンダーごと・タスクリストごとに同期する。
/// 一覧を先に取るのは、取り込んだ予定の入れ先と色を決めるため。
/// </para>
/// <para>
/// <b>同時に2本走らせない。</b>同じ予定を両方が書き換えると、どちらが勝ったのか
/// 分からなくなる。走っている間に呼ばれたら、黙って見送る。
/// </para>
/// </summary>
public sealed class GoogleSyncService(
    CalendarWorkspace workspace,
    GoogleCalendarApi calendars,
    GoogleTasksApi tasks,
    DateTimeOffset? from = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>いま走っているか。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 一度だけ同期する。
    /// <para>すでに走っていれば何もせず null を返す。二重に走らせない。</para>
    /// </summary>
    public async Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;

        IsRunning = true;
        try
        {
            var report = await ImportCalendarListAsync(cancellationToken).ConfigureAwait(false);
            report += await ImportTaskListsAsync(cancellationToken).ConfigureAwait(false);

            foreach (var calendar in Syncable(workspace.Sources.Calendars().Select(c => c.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = new EventSyncEngine(
                    workspace.Events, workspace.Tombstones, workspace.Settings,
                    new CalendarApiGateway(calendars, from));

                report += await engine.SyncAsync(calendar, calendar, cancellationToken).ConfigureAwait(false);
            }

            foreach (var list in Syncable(workspace.Sources.TaskLists().Select(t => t.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = new TaskSyncEngine(
                    workspace.Tasks, workspace.Tombstones, workspace.Settings,
                    new TasksApiGateway(tasks));

                report += await engine.SyncAsync(list, list, cancellationToken).ConfigureAwait(false);
            }

            return report;
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// このアプリの中だけで作ったものは同期しない。
    /// <para>Google に無いものを送ろうとしても行き先が無い。</para>
    /// </summary>
    private static IEnumerable<string> Syncable(IEnumerable<string> ids) =>
        ids.Where(id => !CalendarWorkspace.IsLocalId(id)).ToArray();

    /// <summary>
    /// Google のカレンダー一覧を取り込む。
    /// <para>
    /// 名前と色をここで受け取る。左パネルの色見本と画面上の帯が、Google で見えている
    /// 色と揃う。<b>チェックの状態と付け替えた名前は上書きしない</b>（要件書 5.5）。
    /// 同期のたびに戻ると使い物にならない。
    /// </para>
    /// </summary>
    private async Task<SyncReport> ImportCalendarListAsync(CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var warnings = new List<string>();

        try
        {
            var order = 0;
            string? pageToken = null;

            do
            {
                var page = await calendars.ListCalendarsAsync(pageToken, cancellationToken).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    if (item.Text("id") is not { } id) continue;

                    var existing = workspace.Sources.FindCalendar(id);

                    workspace.Sources.Upsert(new CalendarSource
                    {
                        Id = id,
                        Summary = item.Text("summary") ?? id,

                        // 付け替えた名前は残す。こちらで変えたものを戻さない
                        SummaryOverride = existing?.SummaryOverride ?? item.Text("summaryOverride"),

                        BackgroundColor = item.Text("backgroundColor") ?? existing?.BackgroundColor,
                        ForegroundColor = item.Text("foregroundColor") ?? existing?.ForegroundColor,
                        IsPrimary = item.Flag("primary"),

                        // チェックを外したカレンダーが同期のたびに戻らないようにする
                        IsVisible = existing?.IsVisible ?? true,

                        SortOrder = existing?.SortOrder ?? order++,
                        GoogleRaw = GoogleJson.Normalize(item),
                        UpdatedAt = DateTimeOffset.Now,
                    });

                    if (existing is null) created++;
                    else updated++;
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is { Length: > 0 });
        }
        catch (GoogleApiException ex)
        {
            // 一覧を取れなくても、すでに知っているカレンダーの同期は続けられる
            warnings.Add($"カレンダー一覧を取れませんでした: {ex.Reason}");
        }

        return new SyncReport { CreatedLocal = created, UpdatedLocal = updated, Warnings = warnings };
    }

    /// <summary>Google のタスクリスト一覧を取り込む。</summary>
    private async Task<SyncReport> ImportTaskListsAsync(CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var warnings = new List<string>();

        try
        {
            var order = 0;
            string? pageToken = null;

            do
            {
                var page = await tasks.ListTaskListsAsync(pageToken, cancellationToken).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    if (item.Text("id") is not { } id) continue;

                    var existing = workspace.Sources.FindTaskList(id);

                    workspace.Sources.Upsert(new TaskListSource
                    {
                        Id = id,
                        Title = item.Text("title") ?? id,
                        IsVisible = existing?.IsVisible ?? true,
                        SortOrder = existing?.SortOrder ?? order++,
                        GoogleRaw = GoogleJson.Normalize(item),
                        UpdatedAt = DateTimeOffset.Now,
                    });

                    if (existing is null) created++;
                    else updated++;
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is { Length: > 0 });
        }
        catch (GoogleApiException ex)
        {
            warnings.Add($"タスクリスト一覧を取れませんでした: {ex.Reason}");
        }

        return new SyncReport { CreatedLocal = created, UpdatedLocal = updated, Warnings = warnings };
    }

    public void Dispose() => _gate.Dispose();
}
