using System.Net;
using Kado.Data.Models;
using Kado.Google.Sync;
using Kado.Presentation.Sync;

namespace Kado.Presentation.Tests;

/// <summary>
/// カレンダーごと・リストごとの同期のまとめ役。
/// <para>
/// <b>1つが読めないだけで全体を止めない</b>ことが肝心。実機で、誕生日のような
/// 特殊なカレンダーが 404 を返し、他の予定まで一件も入らない状態になった。
/// </para>
/// </summary>
public class GoogleSyncServiceTests : IDisposable
{
    private sealed class FixedToken : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("ya29.test");
    }

    /// <summary>行き先ごとに応答を出し分ける。</summary>
    private sealed class RoutingHandler(Func<string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        /// <summary>応答を遅らせる。走っている最中に呼ばれる状況を作る。</summary>
        public TimeSpan Delay { get; set; }

        public List<string> Seen { get; } = [];

        /// <summary>書き込みに行った先。送っていないことを確かめるのに使う。</summary>
        public List<string> Wrote { get; } = [];

        /// <summary>
        /// 書き込みだけ、既定の応答より先に割り込ませたいとき。
        /// <para>null を返せば既定の応答（作成・イベント更新）にそのまま任せる。</para>
        /// </summary>
        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)?>? WriteRoute { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Seen.Add(url);

            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);

            if (request.Method != HttpMethod.Get)
            {
                Wrote.Add(url);

                if (WriteRoute?.Invoke(request) is { } routed)
                {
                    return new HttpResponseMessage(routed.Status)
                    {
                        Content = new StringContent(routed.Body, System.Text.Encoding.UTF8, "application/json"),
                    };
                }

                // カレンダーを作ったときの応答。イベントとは形が違う
                if (url.EndsWith("/calendars", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"id":"created-cal","summary":"Kado","accessRole":"owner"}""",
                            System.Text.Encoding.UTF8, "application/json"),
                    };
                }

                // 作った・直したときの応答。id を返さないと、控えを更新できない
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"id":"created-1","summary":"送ったもの","status":"confirmed",
                         "updated":"2026-09-19T00:00:00.000Z",
                         "start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}
                        """,
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }

            var (status, body) = respond(url);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly TestWorkspace _test = TestWorkspace.Create();

    public void Dispose() => _test.Dispose();

    /// <summary>「読める」「読めない」の2つを持つカレンダー一覧。</summary>
    private const string TwoCalendars = """
        {"items":[
          {"id":"yomeru@group.calendar.google.com","summary":"仕事","accessRole":"owner",
           "backgroundColor":"#2f6fed"},
          {"id":"yomenai@group.v.calendar.google.com","summary":"誕生日","accessRole":"reader"}
        ]}
        """;

    private GoogleSyncService Create(RoutingHandler handler)
    {
        var http = new HttpClient(handler);
        var token = new FixedToken();

        return new GoogleSyncService(
            _test.Workspace, new GoogleCalendarApi(http, token), new GoogleTasksApi(http, token));
    }

    private static (HttpStatusCode, string) Route(string url) => url switch
    {
        _ when url.Contains("calendarList", StringComparison.Ordinal) => (HttpStatusCode.OK, TwoCalendars),
        _ when url.Contains("/colors", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"calendar":{}}"""),

        // 誕生日のような特殊なカレンダーは、一覧に出ても中身を取れないことがある
        _ when url.Contains("yomenai", StringComparison.Ordinal) =>
            (HttpStatusCode.NotFound, """{"error":{"errors":[{"reason":"notFound"}]}}"""),

        _ when url.Contains("yomeru", StringComparison.Ordinal) => (HttpStatusCode.OK, """
            {"items":[{"id":"g1","summary":"定例","status":"confirmed",
              "start":{"date":"2026-09-24"},"end":{"date":"2026-09-25"}}],
             "nextSyncToken":"t1"}
            """),

        _ when url.Contains("users/@me/lists", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"items":[]}"""),
        _ => (HttpStatusCode.OK, """{"items":[]}"""),
    };

    [Fact]
    public async Task 読めないカレンダーがあっても他は取り込める()
    {
        using var service = Create(new RoutingHandler(Route));

        var report = await service.SyncAsync();

        Assert.NotNull(report);

        // 読めたほうの予定は入る。ここが 0 だと、1件の失敗で全滅している
        var stored = Assert.Single(_test.Workspace.Events.All());
        Assert.Equal("定例", stored.Title);
    }

    [Fact]
    public async Task 読めなかったことは黙らせない()
    {
        using var service = Create(new RoutingHandler(Route));

        var report = await service.SyncAsync();

        var warning = Assert.Single(report!.Warnings);

        // どのカレンダーが読めなかったのか分かるようにする
        Assert.Contains("誕生日", warning, StringComparison.Ordinal);
        Assert.Contains("notFound", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task カレンダー一覧を取り込む()
    {
        using var service = Create(new RoutingHandler(Route));

        await service.SyncAsync();

        var names = _test.Workspace.Sources.Calendars().Select(c => c.DisplayName).ToArray();

        Assert.Contains("仕事", names);
        Assert.Contains("誕生日", names);
    }

    [Fact]
    public async Task 読むだけのカレンダーへは送らない()
    {
        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        // accessRole が reader のカレンダーへ書き込みに行っていないこと
        Assert.DoesNotContain(handler.Wrote, url => url.Contains("yomenai", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 同時に二本走らせない()
    {
        // 遅らせないと、1本目が終わってから2本目が始まってしまい、重ならない
        var handler = new RoutingHandler(Route) { Delay = TimeSpan.FromMilliseconds(50) };
        using var service = Create(handler);

        var first = service.SyncAsync();
        var second = service.SyncAsync();

        var results = await Task.WhenAll(first, second);

        // 同じ予定を両方が書き換えると、どちらが勝ったのか分からなくなる。
        // 後から来たほうは何もせず引き下がる
        Assert.Single(results, r => r is not null);
        Assert.Single(results, r => r is null);
    }

    [Fact]
    public async Task このアプリだけのカレンダーは同期しない()
    {
        // 既定のカレンダーとタスクリストが用意された状態にする。
        // 実機で「「マイカレンダー」を同期できません（notFound）」が出た
        _test.Workspace.EnsureSources();

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        var report = await service.SyncAsync();

        // 行き先の無いものを問い合わせに行かない
        Assert.DoesNotContain(handler.Seen, url =>
            url.Contains(CalendarWorkspace.DefaultCalendarName, StringComparison.Ordinal) ||
            url.Contains(CalendarWorkspace.DefaultTaskListName, StringComparison.Ordinal) ||
            url.Contains(CalendarWorkspace.LocalIdPrefix, StringComparison.Ordinal));

        // 読めない誕生日カレンダーの1件だけ。こちらのものは警告にならない
        Assert.Single(report!.Warnings);
    }

    [Fact]
    public async Task 印が付く前に作られた既定のカレンダーも同期しない()
    {
        // 古い版は local: の印を付けずに作っていた。ID の形で決めると、
        // 手元に残ったこれを Google に問い合わせに行って notFound になる
        _test.Workspace.Sources.Upsert(new Kado.Data.Models.CalendarSource
        {
            Id = "マイカレンダー", Summary = "マイカレンダー", UpdatedAt = DateTimeOffset.Now,
        });

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.DoesNotContain(handler.Seen, url =>
            url.Contains("%E3%83%9E%E3%82%A4", StringComparison.Ordinal) ||
            url.Contains("マイカレンダー", StringComparison.Ordinal));

        Assert.DoesNotContain(report!.Warnings, w =>
            w.Contains("マイカレンダー", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 実働日の入れ先を Google 側へ移す
    //
    // 繋ぐ前に取り込むとこのアプリの中に入る。繋いだあとは Google の同じ名前の
    // カレンダーへ集めたい。旧 inaCalendar と同じ場所になる
    // ------------------------------------------------------------------

    [Fact]
    public async Task 繋いだら実働日の入れ先を_Google_に作って移す()
    {
        var ina = _test.Workspace.CreateCalendar(CalendarWorkspace.WorkingDayCalendarName);
        _test.Workspace.AddEvent(new Kado.Data.Models.CalendarEvent
        {
            Id = "workingday:20260924:仕様期限", Title = "仕様期限",
            Date = new DateOnly(2026, 9, 24), CalendarId = ina.Id,
            Source = CalendarWorkspace.WorkingDaySource,
        });

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        // 作りに行っている
        Assert.Contains(handler.Wrote, url => url.EndsWith("/calendars", StringComparison.Ordinal));

        // 名前が同じものが2つ並ばない。こちらの分は畳む
        var named = _test.Workspace.WorkingDayCalendars();
        var moved = Assert.Single(named);
        Assert.False(CalendarWorkspace.IsLocal(moved));

        // 中の予定ごと移っている
        Assert.Equal(moved.Id, _test.Workspace.Events.Find("workingday:20260924:仕様期限")!.CalendarId);
    }

    [Fact]
    public async Task 実働日の入れ先が無ければ_Google_に作らない()
    {
        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        // 実働日データを使わない人のために、空のカレンダーを勝手に作らない
        Assert.DoesNotContain(handler.Wrote, url => url.EndsWith("/calendars", StringComparison.Ordinal));
    }

    [Fact]
    public async Task すでに_Google_にあれば作らない()
    {
        _test.Workspace.Sources.Upsert(new Kado.Data.Models.CalendarSource
        {
            Id = "ina@group.calendar.google.com",
            Summary = CalendarWorkspace.WorkingDayCalendarName,
            GoogleRaw = """{"id":"ina@group.calendar.google.com","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        Assert.DoesNotContain(handler.Wrote, url => url.EndsWith("/calendars", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 祝日のカレンダーは初めから外しておく()
    {
        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """
                {"items":[
                  {"id":"ja.japanese#holiday@group.v.calendar.google.com","summary":"日本の祝日",
                   "accessRole":"reader"},
                  {"id":"shigoto@group.calendar.google.com","summary":"仕事","accessRole":"owner"}
                ]}
                """)
            : Route(url));

        using var service = Create(handler);
        await service.SyncAsync();

        // 祝日はアプリの中で計算して添え書きとして出す。予定としても並ぶと二重になる
        var holidays = _test.Workspace.Sources.Calendars()
            .Single(c => c.DisplayName == "日本の祝日");

        Assert.False(holidays.IsVisible);

        // ほかのカレンダーは今までどおり出す
        Assert.True(_test.Workspace.Sources.Calendars().Single(c => c.DisplayName == "仕事").IsVisible);
    }


    [Fact]
    public async Task Google_から消えたカレンダーは一覧から外す()
    {
        // 自分で Google 側のカレンダーを消した状況。控えが残ったままだと、
        // 同期のたびにそれを読みに行って notFound で返され、
        // 「一部を伝えられません」が毎回出る
        _test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "kieta@group.calendar.google.com",
            Summary = "消したカレンダー",
            GoogleRaw = """{"id":"kieta@group.calendar.google.com","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });
        _test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "e1", Title = "消えるはずの予定", Date = new DateOnly(2026, 9, 24),
            CalendarId = "kieta@group.calendar.google.com",
        });

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.DoesNotContain(_test.Workspace.Sources.Calendars(),
            c => c.Id == "kieta@group.calendar.google.com");

        // 中の予定も残さない。どのカレンダーにも属さない予定になると、画面から消せない
        Assert.Null(_test.Workspace.Events.Find("e1"));

        Assert.Contains(report!.Warnings, w =>
            w.Contains("消したカレンダー", StringComparison.Ordinal) &&
            w.Contains("一覧から外しました", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 一覧を取れなかったときは何も外さない()
    {
        _test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = "yomeru@group.calendar.google.com",
            Summary = "仕事",
            GoogleRaw = """{"id":"yomeru@group.calendar.google.com","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

        // 一覧だけ落ちる。読めなかっただけのカレンダーを消してはいけない
        var handler = new RoutingHandler(url =>
            url.Contains("calendarList", StringComparison.Ordinal)
                ? (HttpStatusCode.InternalServerError, """{"error":{"errors":[{"reason":"backendError"}]}}""")
                : Route(url));

        using var service = Create(handler);
        await service.SyncAsync();

        Assert.Contains(_test.Workspace.Sources.Calendars(),
            c => c.Id == "yomeru@group.calendar.google.com");
    }

    [Fact]
    public async Task このアプリの中だけのカレンダーは外さない()
    {
        var local = _test.Workspace.CreateCalendar("マイカレンダー");

        var handler = new RoutingHandler(Route);
        using var service = Create(handler);

        await service.SyncAsync();

        Assert.Contains(_test.Workspace.Sources.Calendars(), c => c.Id == local.Id);
    }

    [Fact]
    public async Task 旧い名前しか無ければ手で変えるよう知らせる()
    {
        SeedLegacyGoogleCalendar();
        _test.Workspace.AddEvent(new CalendarEvent
        {
            Id = "workingday:20260924:仕様期限", Title = "仕様期限",
            Date = new DateOnly(2026, 9, 24), CalendarId = LegacyCalendarId,
            Source = CalendarWorkspace.WorkingDaySource,
        });

        var handler = new RoutingHandler(url => RouteLegacy(url));
        using var service = Create(handler);

        var report = await service.SyncAsync();

        Assert.Contains(report!.Warnings, w =>
            w.Contains("実働日の入れ先が見つかりません", StringComparison.Ordinal) &&
            w.Contains(CalendarWorkspace.LegacyWorkingDayCalendarName, StringComparison.Ordinal) &&
            w.Contains(CalendarWorkspace.WorkingDayCalendarName, StringComparison.Ordinal) &&
            w.Contains("1 件", StringComparison.Ordinal));

        // 勝手に改名しにいかない。断られるだけの書き込みは投げない
        Assert.DoesNotContain(handler.Wrote, url =>
            url.Contains("/calendars/ina%40group.calendar.google.com", StringComparison.Ordinal) &&
            !url.Contains("/events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 入れ先が見つかっていれば案内は出さない()
    {
        _test.Workspace.Sources.Upsert(new CalendarSource
        {
            Id = LegacyCalendarId,
            Summary = CalendarWorkspace.WorkingDayCalendarName,
            GoogleRaw = $$"""{"id":"{{LegacyCalendarId}}","summary":"Kado","accessRole":"owner"}""",
            UpdatedAt = DateTimeOffset.Now,
        });

        var handler = new RoutingHandler(url => url.Contains("calendarList", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, $$"""
                {"items":[{"id":"{{LegacyCalendarId}}","summary":"Kado","accessRole":"owner"}]}
                """)
            : url.Contains("/colors", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"calendar":{}}""")
                : (HttpStatusCode.OK, """{"items":[]}"""));

        using var service = Create(handler);
        var report = await service.SyncAsync();

        Assert.DoesNotContain(report!.Warnings, w =>
            w.Contains("実働日の入れ先が見つかりません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 呼び名を送れなくても相手の姿で消さない()
    {
        // こちらで付けた呼び名を Google へ送ろうとして断られる状況。
        // ここで相手の姿を取り込み直すと、付けた呼び名がその場で消えていた
        var legacy = SeedLegacyGoogleCalendar();
        _test.Workspace.Sources.Upsert(legacy with
        {
            SummaryOverride = "私の実働日",
            UpdatedAt = DateTimeOffset.Now,
        });

        var handler = new RoutingHandler(url => RouteLegacy(url))
        {
            WriteRoute = request => IsCalendarListPatch(request)
                ? (HttpStatusCode.Forbidden,
                   """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}""")
                : null,
        };

        using var service = Create(handler);
        await service.SyncAsync();

        var stored = _test.Workspace.Sources.Calendars().Single(c => c.Id == LegacyCalendarId);
        Assert.Equal("私の実働日", stored.SummaryOverride);
    }

    // ------------------------------------------------------------------
    // 実働日カレンダーの旧い名前「inaCalendar」
    //
    // 入れ先は名前で見分けている。アプリ名を Kado に改めたので、前の道具が
    // 作った「inaCalendar」のままだと入れ先として認識されない。こちらから
    // 改名はできない（権限が無い）ので、手で変えてもらう案内を出す
    // ------------------------------------------------------------------

    private const string LegacyCalendarId = "ina@group.calendar.google.com";

    /// <summary>「inaCalendar」が Google 側にある状態を作る。</summary>
    private CalendarSource SeedLegacyGoogleCalendar(string accessRole = "owner")
    {
        var value = new CalendarSource
        {
            Id = LegacyCalendarId,
            Summary = CalendarWorkspace.LegacyWorkingDayCalendarName,
            GoogleRaw = $$"""
                {"id":"{{LegacyCalendarId}}","summary":"inaCalendar","accessRole":"{{accessRole}}"}
                """,
            UpdatedAt = DateTimeOffset.Now,
        };

        _test.Workspace.Sources.Upsert(value);
        return value;
    }

    /// <summary>「inaCalendar」の一覧・色・イベント一覧に答える。書き込みは呼ぶ側が足す。</summary>
    private static (HttpStatusCode, string) RouteLegacy(string url, string accessRole = "owner") => url switch
    {
        _ when url.Contains("calendarList", StringComparison.Ordinal) => (HttpStatusCode.OK, $$"""
            {"items":[{"id":"{{LegacyCalendarId}}","summary":"inaCalendar","accessRole":"{{accessRole}}"}]}
            """),
        _ when url.Contains("/colors", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"calendar":{}}"""),
        _ => (HttpStatusCode.OK, """{"items":[]}"""),
    };

    /// <summary>一覧側の個人設定（<c>calendarList</c>）への PATCH か。</summary>
    private static bool IsCalendarListPatch(HttpRequestMessage request) =>
        request.Method == HttpMethod.Patch &&
        request.RequestUri!.ToString().Contains("calendarList", StringComparison.Ordinal);

}
