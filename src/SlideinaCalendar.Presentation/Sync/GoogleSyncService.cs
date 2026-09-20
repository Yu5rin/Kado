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
            report += await MoveWorkingDayCalendarToGoogleAsync(cancellationToken).ConfigureAwait(false);

            // 1つが読めないだけで全体を止めない。誕生日のような特殊なカレンダーは
            // 一覧に出ても中身を取れないことがある。そこで止まると、他の予定まで入らない
            foreach (var calendar in workspace.Sources.Calendars().Where(c => IsOnGoogle(c.GoogleRaw)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = new EventSyncEngine(
                    workspace.Events, workspace.Tombstones, workspace.Settings,
                    new CalendarApiGateway(calendars, from));

                report += await RunAsync(
                    () => engine.SyncAsync(calendar.Id, calendar.Id, cancellationToken, IsReadOnly(calendar)),
                    calendar.DisplayName,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var list in workspace.Sources.TaskLists()
                         .Where(t => IsOnGoogle(t.GoogleRaw)).Select(t => t.Id).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = new TaskSyncEngine(
                    workspace.Tasks, workspace.Tombstones, workspace.Settings,
                    new TasksApiGateway(tasks));

                report += await RunAsync(
                    () => engine.SyncAsync(list, list, cancellationToken), list, cancellationToken)
                    .ConfigureAwait(false);
            }

            PruneTombstones();

            return report;
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
        }
    }

    /// <summary>伝えられないまま残った削除の記録を、どれだけ持っておくか。</summary>
    private static readonly TimeSpan TombstoneLife = TimeSpan.FromDays(90);

    /// <summary>
    /// 古い削除の記録を片付ける。
    /// <para>
    /// ふつうは相手へ伝え終わった時点で消える。残るのは伝えようがなかったものだけ
    /// （連携を外したあとに消した、持ち主のカレンダーがもう無い、など）。放っておくと
    /// 溜まり続け、毎回の同期で無駄な問い合わせを生む。
    /// </para>
    /// <para>
    /// <b>短くしすぎない。</b>まだ伝えていない削除を捨てると、次の取り込みで予定が復活する。
    /// </para>
    /// </summary>
    private void PruneTombstones() =>
        workspace.Tombstones.Prune(DateTimeOffset.Now - TombstoneLife);

    /// <summary>
    /// 実働日データの入れ先を Google 側へ移す。
    /// <para>
    /// 繋ぐ前に取り込むと、マイルストーンはこのアプリの中の「inaCalendar」に入る。繋いだ
    /// あとは Google 側の同じ名前のカレンダーへ集めたい。旧 inaCalendar と同じ場所になり、
    /// 他の端末やブラウザからも見える。
    /// </para>
    /// <para>
    /// Google 側に同じ名前のカレンダーが無ければ作る。<b>こちらに入れ先が無いときは何もしない。</b>
    /// 実働日データを使わない人のために、空のカレンダーを勝手に作らない。
    /// </para>
    /// </summary>
    private async Task<SyncReport> MoveWorkingDayCalendarToGoogleAsync(CancellationToken cancellationToken)
    {
        var named = workspace.WorkingDayCalendars();

        // すでに Google 側にある。移す先ができているので何もしない
        if (named.Any(c => !CalendarWorkspace.IsLocal(c))) return new SyncReport();

        if (named.FirstOrDefault() is not { } local) return new SyncReport();

        try
        {
            var created = await calendars
                .InsertCalendarAsync(CalendarWorkspace.WorkingDayCalendarName, cancellationToken)
                .ConfigureAwait(false);

            if (created.Text("id") is not { Length: > 0 } id) return new SyncReport();

            workspace.Sources.Upsert(new CalendarSource
            {
                Id = id,
                // 頼んだ名前をそのまま控える。日付の行に出すかどうかは名前で見分けているので、
                // 相手が違う名前を返してきたら特別な表示が黙って止まる
                Summary = CalendarWorkspace.WorkingDayCalendarName,
                BackgroundColor = local.BackgroundColor,
                SortOrder = local.SortOrder,
                GoogleRaw = GoogleJson.Normalize(created),
                UpdatedAt = DateTimeOffset.Now,
            });

            // 中の予定ごと移して、こちらの分は畳む。名前が同じものが2つ並ばないようにする
            var moved = workspace.Sources.DeleteCalendar(local.Id, id);

            return new SyncReport
            {
                CreatedRemote = 1,
                Warnings = [$"実働日の入れ先を Google の「{CalendarWorkspace.WorkingDayCalendarName}」に移しました（{moved} 件）"],
            };
        }
        catch (GoogleApiException ex)
        {
            // 作れなくても、こちらの中には入れ先がある。次の同期でまた試す
            return new SyncReport
            {
                Warnings = [$"Google に「{CalendarWorkspace.WorkingDayCalendarName}」を作れませんでした（{ex.Reason}）"],
            };
        }
    }

    /// <summary>
    /// Google が配っている祝日のカレンダーか。
    /// <para>「日本の祝日」などの ID は <c>...#holiday@group.v.calendar.google.com</c> の形。</para>
    /// </summary>
    private static bool IsHolidayCalendar(string id) =>
        id.Contains("#holiday@", StringComparison.Ordinal);

    /// <summary>
    /// Google 側にあるものか。同期するのはこれだけ。
    /// <para>
    /// 判断は<b>最後に受け取った姿を持っているか</b>で行う。一覧の取り込みは必ず
    /// これを書くので、持っていないものは Google の一覧に載っていない。
    /// </para>
    /// <para>
    /// ID の形では決めない。このアプリの印が付く前に作られた既定のカレンダーが
    /// 手元に残っていて、それを Google に問い合わせると notFound になっていた。
    /// </para>
    /// </summary>
    private static bool IsOnGoogle(string? googleRaw) => googleRaw is { Length: > 0 };

    /// <summary>
    /// 1つぶんを走らせる。転んでも他を巻き添えにしない。
    /// <para>
    /// カレンダーは1つずつ独立している。1つが読めないからといって、他のカレンダーの
    /// 予定まで入らないのは困る。何が起きたかは残したうえで次へ進む。
    /// </para>
    /// </summary>
    private static async Task<SyncReport> RunAsync(
        Func<Task<SyncReport>> work, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            return new SyncReport { Warnings = [$"「{name}」を同期できません（{ex.Reason}）"] };
        }
        catch (HttpRequestException ex)
        {
            return new SyncReport { Warnings = [$"「{name}」に繋がりません（{ex.Message}）"] };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 止めたのはこちら。握り潰さず上へ返す
            throw;
        }
        catch (Exception ex)
        {
            // 想定していない転び方をしても、他のカレンダーは続ける。
            // 1つのつまずきで一件も入らないより、入るものを入れて何が起きたかを残す
            return new SyncReport { Warnings = [$"「{name}」で想定外の失敗（{ex.GetType().Name}: {ex.Message}）"] };
        }
    }

    /// <summary>
    /// こちらから書けないカレンダーか。
    /// <para>
    /// 祝日・誕生日・他人から共有されたものは読むだけ。送ろうとしても断られる。
    /// 判断は取り込んだときの <c>accessRole</c> で行う。
    /// </para>
    /// </summary>
    private static bool IsReadOnly(CalendarSource calendar)
    {
        if (calendar.GoogleRaw is not { Length: > 0 } raw) return false;

        try
        {
            var role = JsonDocument.Parse(raw).RootElement.Text("accessRole");

            // owner と writer だけが書ける。reader / freeBusyReader は読むだけ
            return role is not null &&
                   !string.Equals(role, "owner", StringComparison.Ordinal) &&
                   !string.Equals(role, "writer", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            // 読めないなら書けると見なす。書けないものへ送れば断られるだけで、
            // 書けるものを読み取り専用にしてしまうより害が小さい
            return false;
        }
    }

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

                        // チェックを外したカレンダーが同期のたびに戻らないようにする。
                        // 祝日のカレンダーだけ、初めて取り込むときは外しておく。祝日は
                        // アプリの中で計算して日付の添え書きとして出すので、予定としても
                        // 並ぶと二重になる。見たければチェックを入れればよい
                        IsVisible = existing?.IsVisible ?? !IsHolidayCalendar(id),

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
