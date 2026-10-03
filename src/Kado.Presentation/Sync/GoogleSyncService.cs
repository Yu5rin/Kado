using System.Text.Json;
using System.Text.Json.Nodes;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Google.Sync;

namespace Kado.Presentation.Sync;

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

            // 入れ先が決まったあとで見る。ここまでで見つからなければ案内を出す
            report += WarnAboutLegacyWorkingDayCalendar();

            // 1つが読めないだけで全体を止めない。誕生日のような特殊なカレンダーは
            // 一覧に出ても中身を取れないことがある。そこで止まると、他の予定まで入らない
            // 一覧から外れたもの（送っていない中身を残してあるだけ）は同期しない
            foreach (var calendar in workspace.Sources.Calendars()
                         .Where(c => IsOnGoogle(c.GoogleRaw) && !c.IsDetached))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = new EventSyncEngine(
                    workspace.Events, workspace.Tombstones, workspace.Settings,
                    new CalendarApiGateway(calendars, from));

                report += await RunAsync(
                    () => engine.SyncAsync(calendar.Id, calendar.Id, cancellationToken, calendar.IsReadOnly),
                    calendar.DisplayName,
                    cancellationToken).ConfigureAwait(false);
            }

            // 一覧から外れたもの（送っていない中身を残してあるだけ）は同期しない
            foreach (var list in workspace.Sources.TaskLists()
                         .Where(t => IsOnGoogle(t.GoogleRaw) && !t.IsDetached).Select(t => t.Id).ToArray())
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
    /// 実働日の入れ先が見つからないときに、旧い名前のカレンダーがあれば知らせる。
    /// <para>
    /// 実働日とマイルストーンの入れ先は、カレンダーの<b>名前</b>で見分けている
    /// （<see cref="CalendarWorkspace.WorkingDayCalendarName"/>）。アプリ名を Kado に
    /// 改めたので見分ける名前も Kado になったが、前の道具が作った「inaCalendar」が
    /// そのまま残っていることがある。名前が合わないと、実働日もマイルストーンも
    /// <b>黙って画面から消える</b>。
    /// </para>
    /// <para>
    /// <b>こちらから改名はしない。</b>Google のカレンダーそのものの名前を書き換えるには
    /// <c>calendar</c> の全権か、アプリ自身が作ったカレンダーであることが要る。
    /// Kado が持っているのは予定と一覧を読み書きするぶんだけなので、前の道具が作った
    /// カレンダーは改名できない。試しても断られるだけの処理を抱えるより、
    /// <b>手で変えてもらう案内を1行出す</b>ほうが確かで、何が起きているかも伝わる。
    /// </para>
    /// </summary>
    private SyncReport WarnAboutLegacyWorkingDayCalendar()
    {
        // 入れ先が見つかっている。案内は要らない
        if (workspace.WorkingDayCalendars().Count > 0) return new SyncReport();

        var legacy = workspace.Sources.Calendars().FirstOrDefault(c => string.Equals(
            c.DisplayName, CalendarWorkspace.LegacyWorkingDayCalendarName, StringComparison.Ordinal));

        if (legacy is null) return new SyncReport();

        var count = workspace.Events.All()
            .Count(e => string.Equals(e.CalendarId, legacy.Id, StringComparison.Ordinal));

        return new SyncReport
        {
            Warnings =
            [
                $"実働日の入れ先が見つかりません。Google カレンダーで" +
                $"「{CalendarWorkspace.LegacyWorkingDayCalendarName}」を" +
                $"「{CalendarWorkspace.WorkingDayCalendarName}」に変えてください" +
                $"（{count} 件がその中にあります）",
            ],
        };
    }

    /// <summary>
    /// 実働日データの入れ先を Google 側へ移す。
    /// <para>
    /// 繋ぐ前に取り込むと、マイルストーンはこのアプリの中の「Kado」に入る。繋いだ
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listed = false;

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

                    seen.Add(id);

                    var existing = workspace.Sources.FindCalendar(id);

                    // 一覧から外れていたものが戻った、または隠していたものを表示に戻した。
                    // 見ていない間の変更は、古い差分の印では取れない。印を捨てて全件取り直す
                    if (existing is not null && ReturnedToList(existing, item))
                    {
                        workspace.Settings.SetSyncState(EventSyncEngine.TokenKey(id), string.Empty);

                        if (existing.IsDetached)
                        {
                            workspace.Sources.SetCalendarDetached(id, false);
                            updated++;
                        }
                    }

                    // こちらで名前や色を変えていたら、先に相手へ送る。
                    // 送る前に上書きすると、変えたことが消えてしまう
                    var pushedSettings = existing is null
                        ? PushOutcome.Nothing
                        : await PushCalendarSettingsAsync(existing, item, cancellationToken)
                            .ConfigureAwait(false);

                    if (pushedSettings is PushOutcome.Sent)
                    {
                        pushed++;
                        continue;
                    }

                    // 断られたときも、このカレンダー自体は取り込む。丸ごと飛ばすと、
                    // Google 側で名前・色・権限が変わっても Kado に入ってこなくなり、
                    // 警告も出ない。ただし、こちらで変えた呼び名と色はこちらの値を残す。
                    // 送れないだけで、変えたことまで取り消す道理は無い。次の同期でまた送り直す
                    var refused = pushedSettings is PushOutcome.Refused;

                    var wanted = new CalendarSource
                    {
                        Id = id,
                        Summary = item.Text("summary") ?? id,
                        SummaryOverride = refused ? existing?.SummaryOverride : item.Text("summaryOverride"),
                        BackgroundColor = refused
                            ? existing?.BackgroundColor
                            : item.Text("backgroundColor") ?? existing?.BackgroundColor,
                        ForegroundColor = refused
                            ? existing?.ForegroundColor
                            : item.Text("foregroundColor") ?? existing?.ForegroundColor,
                        IsPrimary = item.Flag("primary"),

                        // チェックを外したカレンダーが同期のたびに戻らないようにする。
                        // 祝日のカレンダーだけ、初めて取り込むときは外しておく。祝日は
                        // アプリの中で計算して日付の添え書きとして出すので、予定としても
                        // 並ぶと二重になる。見たければチェックを入れればよい
                        IsVisible = existing?.IsVisible ?? !IsHolidayCalendar(id),

                        SortOrder = existing?.SortOrder ?? order++,
                        GoogleRaw = GoogleJson.Normalize(item),
                        UpdatedAt = DateTimeOffset.Now,
                    };

                    // 中身が前回と同じなら書かない。既知のカレンダーを読み直しただけで
                    // 無条件に「更新」と数えていたころは、同期のたびに UpdatedLocal が
                    // 積み上がり、SyncReport.HasChanges がほぼ常に true になって、
                    // 同期のあとの画面の作り直しを省く仕組みが働かなかった
                    var changed = existing is null || CalendarChanged(existing, wanted);

                    if (changed) workspace.Sources.Upsert(wanted);

                    if (existing is null) created++;
                    else if (changed) updated++;
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is { Length: > 0 });

            listed = true;
        }
        catch (GoogleApiException ex)
        {
            // 一覧を取れなくても、すでに知っているカレンダーの同期は続けられる
            warnings.Add($"カレンダー一覧を取れませんでした: {ex.Reason}");
        }

        // Google から無くなったものを片付ける。
        //
        // 消されたカレンダーの控えが残ったままだと、同期のたびにそれを読みに行って
        // notFound で返され、「一部を伝えられません」が毎回出る。自分で消したのに
        // 消えないので、直しようも無い。
        //
        // <b>一覧を最後まで取れたときだけ見る。</b>途中で切れた一覧を頼りにすると、
        // 読めなかっただけのカレンダーを消してしまう。1件も返ってこなかったときも
        // 触らない。応答が壊れていただけで全部消える、という壊れ方を避ける
        var removedCount = 0;

        if (listed && seen.Count > 0)
        {
            var gone = workspace.Sources.Calendars()
                .Where(c => IsOnGoogle(c.GoogleRaw) && !seen.Contains(c.Id))
                .ToArray();

            var dropped = new List<CalendarSource>();

            foreach (var calendar in gone)
            {
                // 本当に一覧から消えた（購読解除・削除）カレンダーでも、まだ送っていない
                // 予定・編集・削除の記録があるなら、捨てない。捨てると使う人の入力が、
                // 知らせもなく消える。中身を残して同期を止め、確かめてから消してもらう
                var unsent = CountUnsent(calendar);

                if (unsent > 0)
                {
                    if (!calendar.IsDetached)
                    {
                        workspace.Sources.SetCalendarDetached(calendar.Id, true);
                        removedCount++;

                        warnings.Add(
                            $"「{calendar.DisplayName}」が Google の一覧から外れました（購読解除や削除など）。" +
                            $"まだ送っていない予定・変更が {unsent} 件あるため、中身を残して同期を止めました。" +
                            "確かめてから、カレンダーごと消してください");
                    }

                    continue;
                }

                workspace.Sources.DropRemovedCalendar(calendar.Id);
                workspace.Tombstones.ForgetSource(calendar.Id);
                dropped.Add(calendar);
            }

            removedCount += dropped.Count;

            if (dropped.Count > 0)
            {
                warnings.Add(dropped.Count == 1
                    ? $"Google から消えた「{dropped[0].DisplayName}」を一覧から外しました"
                    : $"Google から消えたカレンダー {dropped.Count} 件を一覧から外しました");
            }
        }

        return new SyncReport
        {
            CreatedLocal = created,
            UpdatedLocal = updated,
            UpdatedRemote = pushed,
            // 消えたカレンダーは created/updated に数えない（作った・直したわけではない）。
            // それでも一覧は変わっているので、専用の印を立てる（SyncReport.SourcesChanged を見よ）
            SourcesChanged = removedCount > 0,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 一覧に戻ってきた（または表示に戻した）カレンダーか。
    /// <para>
    /// 一覧から外れた印が付いていたもの、または前回は Google 側で隠れていた
    /// （<c>hidden</c>）のに今回は隠れていないもの。
    /// </para>
    /// </summary>
    private static bool ReturnedToList(CalendarSource existing, JsonElement item) =>
        existing.IsDetached || (IsHiddenOnGoogle(existing.GoogleRaw) && !item.Flag("hidden"));

    /// <summary>控えてある一覧の姿で、Google 側で隠れていたか。</summary>
    private static bool IsHiddenOnGoogle(string? googleRaw)
    {
        if (googleRaw is not { Length: > 0 }) return false;

        try
        {
            using var document = JsonDocument.Parse(googleRaw);

            return document.RootElement.TryGetProperty("hidden", out var hidden) &&
                   hidden.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// まだ Google に送っていない中身の件数。予定と、まだ伝えていない削除の記録。
    /// <para>
    /// 読むだけのカレンダーは送れないので数えない。実働日データから起こした印
    /// （休業日・特別出勤・マイルストーン）は、取り込み直せば戻るので数えない。
    /// 削除の記録は、持ち主がこのカレンダーと分かっているものだけ数える。
    /// </para>
    /// </summary>
    private int CountUnsent(CalendarSource calendar)
    {
        if (calendar.IsReadOnly) return 0;

        var events = workspace.Events.ByCalendarId(calendar.Id)
            .Count(e => !IsRebuiltMark(e.Id) &&
                        (e.GoogleEventId is null ||
                         EventMapper.NeedsPush(e) ||
                         e.PendingAttachments is not null));

        var deletions = workspace.Tombstones.Pending(TombstoneRepository.EventKind, calendar.Id)
            .Count(t => string.Equals(t.SourceId, calendar.Id, StringComparison.Ordinal));

        return events + deletions;
    }

    private static bool IsRebuiltMark(string id) =>
        CalendarWorkspace.IsMilestoneId(id) ||
        CalendarWorkspace.IsClosedDayId(id) ||
        CalendarWorkspace.IsOpenDayId(id);

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
    /// <returns>送ったか、断られたか、送るものが無かったか。</returns>
    private async Task<PushOutcome> PushCalendarSettingsAsync(
        CalendarSource local, JsonElement remote, CancellationToken cancellationToken)
    {
        var body = new JsonObject();

        // 「こちらで変えた」かどうかは、最後に Google から受け取った姿（GoogleRaw）と手元を
        // 見比べて決める。いま届いた姿（remote）と比べてはいけない。Web で色や呼び名を変えると
        // 手元は旧いままなので、いま届いた姿と食い違い、「こちらで変えた」と取り違えて旧い値を
        // 送り返してしまう。変わっていなければ送らず、Google の姿に従う
        var received = ReadReceived(local.GoogleRaw, remote);

        if (!string.Equals(local.SummaryOverride, received.Text("summaryOverride"), StringComparison.Ordinal) &&
            !string.Equals(local.SummaryOverride, remote.Text("summaryOverride"), StringComparison.Ordinal))
        {
            // 空にしたのは使う人が外したということ。消す意思として null を送る
            body["summaryOverride"] = local.SummaryOverride;
        }

        var changedColor = !string.Equals(
            local.BackgroundColor, received.Text("backgroundColor"), StringComparison.OrdinalIgnoreCase);

        if (changedColor)
        {
            var colors = await PaletteAsync(cancellationToken).ConfigureAwait(false);
            var wanted = colors.ClosestCalendarId(local.BackgroundColor);

            // 寄せ先が今の色番号と違うときだけ送る。同じ番号なら見た目は変わらない
            if (wanted is not null && !string.Equals(wanted, remote.Text("colorId"), StringComparison.Ordinal))
            {
                body["colorId"] = wanted;
            }
        }

        if (body.Count == 0) return PushOutcome.Nothing;

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

            return PushOutcome.Sent;
        }
        catch (GoogleApiException)
        {
            // 送れなくても、こちらの見た目は変えたままにしておく。次の同期で出し直す。
            // 呼び出し側はこれを受けて、相手の姿での上書きを見送る
            return PushOutcome.Refused;
        }
    }

    /// <summary>
    /// 最後に Google から受け取った姿。控えが無い（読めない）ときは、いま届いた姿を
    /// 比べる相手にする（そのときは「変えていない」と見なして何も送らない）。
    /// </summary>
    private static JsonElement ReadReceived(string? googleRaw, JsonElement fallback)
    {
        if (googleRaw is not { Length: > 0 }) return fallback;

        try
        {
            return JsonDocument.Parse(googleRaw).RootElement.Clone();
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// <see cref="PushCalendarSettingsAsync"/> の結果。
    /// <para>
    /// 「送った」と「断られた」を分けるためにある。まとめて false で返していたころは、
    /// 断られた直後に呼び出し側が相手の姿で上書きしていた。こちらで変えた呼び名が
    /// その場で消えるので、実働日の入れ先を呼び名で凌いでいる場合は、
    /// <b>実働日とマイルストーンが見えなくなる</b>。
    /// </para>
    /// </summary>
    private enum PushOutcome
    {
        /// <summary>送るものが無かった。相手の姿をそのまま取り込んでよい。</summary>
        Nothing,

        /// <summary>送れた。控えは送った結果で更新してある。</summary>
        Sent,

        /// <summary>断られた。こちらで変えた値はそのまま残す。</summary>
        Refused,
    }

    /// <summary>
    /// 既存の控えと、これから書こうとしている内容とで、実際に違うところがあるか。
    /// <para>
    /// <c>SourceRepository.Upsert(CalendarSource)</c> の SQL は <c>ON CONFLICT</c> の
    /// <c>SET</c> 句に <c>is_visible</c> と <c>notify_default</c> を含めていない
    /// （＝更新では書き換わらず、既存の値がそのまま残る）。<c>IsVisible</c> は
    /// <c>existing?.IsVisible ?? …</c> で必ず <paramref name="existing"/> と同じ値になるので
    /// ここで比べても実害は無いが、<c>NotifyDefault</c> は <paramref name="wanted"/> 側が
    /// 常に既定値（<c>true</c>）になる（この処理は関知しない項目のため）。DB には反映され
    /// ないのに比べてしまうと、通知を切ったカレンダーが同期のたびに「変わった」と誤判定
    /// されるので、<c>NotifyDefault</c> はここでは見ない。
    /// </para>
    /// <para>
    /// <c>IsReadOnly</c> は <c>GoogleRaw</c>（<c>accessRole</c>）から計算する派生プロパティ
    /// なので、<c>GoogleRaw</c> を比べれば自動でカバーされる。<c>UpdatedAt</c> は書くたびに
    /// 変わる値なので比べない。
    /// </para>
    /// </summary>
    private static bool CalendarChanged(CalendarSource existing, CalendarSource wanted) =>
        !string.Equals(existing.Summary, wanted.Summary, StringComparison.Ordinal) ||
        !string.Equals(existing.SummaryOverride, wanted.SummaryOverride, StringComparison.Ordinal) ||
        !string.Equals(existing.BackgroundColor, wanted.BackgroundColor, StringComparison.Ordinal) ||
        !string.Equals(existing.ForegroundColor, wanted.ForegroundColor, StringComparison.Ordinal) ||
        existing.IsPrimary != wanted.IsPrimary ||
        existing.IsVisible != wanted.IsVisible ||
        existing.SortOrder != wanted.SortOrder ||
        !GoogleJson.SameContent(existing.GoogleRaw, wanted.GoogleRaw);

    /// <summary>タスクリスト版。理由は <see cref="CalendarChanged"/> と同じ。</summary>
    private static bool TaskListChanged(TaskListSource existing, TaskListSource wanted) =>
        !string.Equals(existing.Title, wanted.Title, StringComparison.Ordinal) ||
        existing.IsVisible != wanted.IsVisible ||
        existing.SortOrder != wanted.SortOrder ||
        !GoogleJson.SameContent(existing.GoogleRaw, wanted.GoogleRaw);

    /// <summary>
    /// 色の一覧。一度取ったら覚えておく。
    /// <para>めったに変わらないので、同期のたびに取りに行かない。</para>
    /// </summary>
    private async Task<GoogleColors> PaletteAsync(CancellationToken cancellationToken) =>
        _palette ??= await calendars.GetColorsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Google のタスクリスト一覧を取り込む。
    /// <para>
    /// 一覧から消えたリストは、カレンダーと同じに扱う（<see cref="ImportCalendarListAsync"/>）。
    /// 送っていないものが無ければ一覧から外し、あれば捨てずに残して「Google から外れた」印を付け、
    /// 同期を止めて警告する。何もしないと、消えたリストを毎回読みに行って、同期のたびに警告が出る。
    /// </para>
    /// </summary>
    private async Task<SyncReport> ImportTaskListsAsync(CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listed = false;

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

                    seen.Add(id);

                    var existing = workspace.Sources.FindTaskList(id);

                    // 一覧から外れていたものが戻った。見ていない間の変更は、前回の時刻では取れない。
                    // 前回の時刻を捨てて全件を取り直す
                    if (existing is { IsDetached: true })
                    {
                        workspace.Settings.SetSyncState(TaskSyncEngine.SinceKey(id), string.Empty);
                        workspace.Sources.SetTaskListDetached(id, false);
                        updated++;
                    }

                    var wanted = new TaskListSource
                    {
                        Id = id,
                        Title = item.Text("title") ?? id,
                        IsVisible = existing?.IsVisible ?? true,
                        SortOrder = existing?.SortOrder ?? order++,
                        GoogleRaw = GoogleJson.Normalize(item),
                        UpdatedAt = DateTimeOffset.Now,
                    };

                    // 理由はカレンダー一覧側と同じ（ImportCalendarListAsync を見よ）
                    var changed = existing is null || TaskListChanged(existing, wanted);

                    if (changed) workspace.Sources.Upsert(wanted);

                    if (existing is null) created++;
                    else if (changed) updated++;
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is { Length: > 0 });

            listed = true;
        }
        catch (GoogleApiException ex)
        {
            warnings.Add($"タスクリスト一覧を取れませんでした: {ex.Reason}");
        }

        // Google から無くなったリストを片付ける。カレンダーと同じ理由で、
        // 一覧を最後まで取れたときだけ見る。1件も返ってこなかったときも触らない
        var removedCount = 0;

        if (listed && seen.Count > 0)
        {
            var gone = workspace.Sources.TaskLists()
                .Where(t => IsOnGoogle(t.GoogleRaw) && !seen.Contains(t.Id))
                .ToArray();

            var dropped = new List<TaskListSource>();

            foreach (var list in gone)
            {
                // まだ送っていないタスク・編集・削除の記録があるなら、捨てない
                var unsent = CountUnsent(list);

                if (unsent > 0)
                {
                    if (!list.IsDetached)
                    {
                        workspace.Sources.SetTaskListDetached(list.Id, true);
                        removedCount++;

                        warnings.Add(
                            $"「{list.DisplayName}」が Google の一覧から外れました（削除など）。" +
                            $"まだ送っていないタスク・変更が {unsent} 件あるため、中身を残して同期を止めました。" +
                            "確かめてから、タスクリストごと消してください");
                    }

                    continue;
                }

                workspace.Sources.DropRemovedTaskList(list.Id);
                workspace.Tombstones.ForgetSource(list.Id);
                dropped.Add(list);
            }

            removedCount += dropped.Count;

            if (dropped.Count > 0)
            {
                warnings.Add(dropped.Count == 1
                    ? $"Google から消えた「{dropped[0].DisplayName}」を一覧から外しました"
                    : $"Google から消えたタスクリスト {dropped.Count} 件を一覧から外しました");
            }
        }

        return new SyncReport
        {
            CreatedLocal = created,
            UpdatedLocal = updated,
            SourcesChanged = removedCount > 0,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// まだ Google に送っていないタスクの件数。結び付いていないもの、編集したもの、
    /// まだ伝えていない削除の記録（持ち主がこのリストと分かっているもの）。
    /// </summary>
    private int CountUnsent(TaskListSource list)
    {
        var tasksInList = workspace.Tasks.All()
            .Count(t => string.Equals(t.TaskListId, list.Id, StringComparison.Ordinal) &&
                        (t.GoogleTaskId is null || TaskMapper.NeedsPush(t)));

        var deletions = workspace.Tombstones.Pending(TombstoneRepository.TaskKind, list.Id)
            .Count(t => string.Equals(t.SourceId, list.Id, StringComparison.Ordinal));

        return tasksInList + deletions;
    }

    public void Dispose() => _gate.Dispose();
}
