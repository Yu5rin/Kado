using System.Text.Json;
using System.Text.Json.Nodes;
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

    private GoogleColors? _palette;

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
        var pushed = 0;
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

                    // こちらで名前や色を変えていたら、先に相手へ送る。
                    // 送る前に上書きすると、変えたことが消えてしまう
                    if (existing is not null &&
                        await PushCalendarSettingsAsync(existing, item, cancellationToken).ConfigureAwait(false))
                    {
                        pushed++;
                        continue;
                    }

                    workspace.Sources.Upsert(new CalendarSource
                    {
                        Id = id,
                        Summary = item.Text("summary") ?? id,
                        SummaryOverride = item.Text("summaryOverride"),
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

        return new SyncReport
        {
            CreatedLocal = created,
            UpdatedLocal = updated,
            UpdatedRemote = pushed,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// こちらで変えた名前と色を相手へ送る。
    /// <para>
    /// 名前の付け替えと表示色は Google でも<b>その人だけの設定</b>なので、書き戻しても
    /// 共有している相手には影響しない。これを送らないと、こちらで変えた色が同期のたびに
    /// 戻り、左パネルの色見本が言うことを聞かなくなる。
    /// </para>
    /// <para>
    /// 色は任意の <c>#rrggbb</c> をそのままは送れない。Google が持っている番号のうち
    /// <b>いちばん近いもの</b>に寄せる。寄せた結果は次の同期で降ってきて、画面もそれに揃う。
    /// </para>
    /// </summary>
    /// <returns>送ったら true。送るものが無ければ false。</returns>
    private async Task<bool> PushCalendarSettingsAsync(
        CalendarSource local, JsonElement remote, CancellationToken cancellationToken)
    {
        var body = new JsonObject();

        // 相手から受け取った姿と違っていれば、こちらで変えたということ
        if (!string.Equals(local.SummaryOverride, remote.Text("summaryOverride"), StringComparison.Ordinal))
        {
            body["summaryOverride"] = local.SummaryOverride;
        }

        var colors = await PaletteAsync(cancellationToken).ConfigureAwait(false);
        var wanted = colors.ClosestCalendarId(local.BackgroundColor);

        // 寄せ先が今の色番号と違うときだけ送る。同じ番号なら見た目は変わらない
        if (wanted is not null && !string.Equals(wanted, remote.Text("colorId"), StringComparison.Ordinal))
        {
            body["colorId"] = wanted;
        }

        if (body.Count == 0) return false;

        try
        {
            var patched = await calendars
                .PatchCalendarListAsync(local.Id, body, cancellationToken)
                .ConfigureAwait(false);

            // 送った結果をそのまま控える。次の同期で「また変わった」と見ないため
            workspace.Sources.Upsert(local with
            {
                Summary = patched.Text("summary") ?? local.Summary,
                SummaryOverride = patched.Text("summaryOverride"),
                BackgroundColor = patched.Text("backgroundColor") ?? local.BackgroundColor,
                ForegroundColor = patched.Text("foregroundColor") ?? local.ForegroundColor,
                GoogleRaw = GoogleJson.Normalize(patched),
                UpdatedAt = DateTimeOffset.Now,
            });

            return true;
        }
        catch (GoogleApiException)
        {
            // 送れなくても、こちらの見た目は変えたままにしておく。次の同期で出し直す
            return false;
        }
    }

    /// <summary>
    /// 色の一覧。一度取ったら覚えておく。
    /// <para>めったに変わらないので、同期のたびに取りに行かない。</para>
    /// </summary>
    private async Task<GoogleColors> PaletteAsync(CancellationToken cancellationToken) =>
        _palette ??= await calendars.GetColorsAsync(cancellationToken).ConfigureAwait(false);

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
