using System.Globalization;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data.Repositories;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Settings;

/// <summary>配色の選び方。</summary>
public enum ThemeChoice
{
    /// <summary>Windows の設定に合わせる。</summary>
    Auto,

    Light,
    Dark,

    /// <summary>
    /// 夜間。ダークより明るいグレーで、コントラストを落としてある。
    /// <para>真っ暗な画面を長く見ていると目が疲れる、という向き。</para>
    /// </summary>
    Night,
}

/// <summary>
/// 画面まわりの設定。
/// <para>
/// 値はデータベースの <c>settings</c> に入れる。書いた時点で保存するので、
/// 設定画面に「保存」は置かない。変えたらその場で効く。
/// </para>
/// <para>
/// 残す設定は要件書 5.5 で決まっている。ビュー別の表示項目 ON/OFF のような、
/// レイアウトで解くべきものは持たない。通知は仕組みごと作るときに足す
/// （欄だけ先にあると「設定したのに鳴らない」ことになる）。
/// </para>
/// </summary>
public sealed class AppSettings
{
    private const string ThemeKey = "ui.theme";
    private const string WeekStartKey = "ui.week_start";
    private const string DayStartKey = "ui.day_start_hour";
    private const string DayEndKey = "ui.day_end_hour";
    private const string StartupViewKey = "ui.startup_view";
    private const string CountInCalendarDaysKey = "count.calendar_days";
    private const string HourHeightKey = "ui.hour_height";
    private const string FeedUrlKey = "workday.feed_url";
    private const string FeedAutoKey = "workday.feed_auto";
    private const string FeedCheckedKey = "workday.feed_checked";
    private const string NotifyKey = "notify.enabled";
    private const string NotifyLeadKey = "notify.lead_minutes";
    private const string SummaryKey = "notify.summary_enabled";
    private const string SummaryTimeKey = "notify.summary_time";
    private const string NotifySoundKey = "notify.sound";
    private const string DefaultCalendarKey = "ui.default_calendar";
    private const string DefaultTaskListKey = "ui.default_task_list";
    private const string YearLayoutKey = "ui.year_layout";
    private const string CloseToTrayKey = "ui.close_to_tray";
    private const string SlideOutOnLeaveKey = "shell.slide_out_on_leave";
    private const string MinWidthKey = "shell.min_width";
    private const string SlimShareKey = "ui.slim_calendar_share";
    private const string WindowPanesKey = "ui.panes.window";
    private const string EdgePanesKey = "ui.panes.edge";
    private const string CheckForUpdateOnStartupKey = "update.check_on_startup";
    private const string WorkdayOffsetPlansKey = "workday.offset_plans";
    private const string WorkdaySelectedPlanKey = "workday.offset_plan_selected";

    /// <summary>
    /// 表示時間帯の既定。
    /// <para>
    /// 一日ぶんをすべて出す。決まった時間帯だけを出すと、早朝や夜の予定が画面から
    /// 消えて気づけない。高さは画面に合わせて割り付けるので、24時間でも収まる。
    /// 狭めたい人は設定で変えられる。
    /// </para>
    /// </summary>
    public const int DefaultDayStartHour = 0;

    /// <summary>表示時間帯の既定の終わり。</summary>
    public const int DefaultDayEndHour = 24;

    private readonly SettingsRepository _store;

    private ThemeChoice _theme;
    private DayOfWeek _weekStart;
    private YearLayout _yearLayout;
    private bool _closeToTray = true;
    private bool _slideOutOnLeave = true;
    private int _minWidth = DefaultMinWidth;
    private int _dayStartHour;
    private int _dayEndHour;
    private CalendarView _startupView;
    private bool _countInCalendarDays;
    private int _hourHeight;
    private string _feedUrl = string.Empty;
    private bool _feedAuto;
    private bool _notifyEnabled;
    private int _notifyLeadMinutes;
    private bool _summaryEnabled;
    private TimeOnly _summaryTime;
    private bool _notifySound;
    private bool _checkForUpdateOnStartup;

    public AppSettings(SettingsRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _theme = Read(ThemeKey, ThemeChoice.Auto);
        _weekStart = Read(WeekStartKey, DayOfWeek.Sunday);
        _startupView = Read(StartupViewKey, CalendarView.Month);
        _yearLayout = Read(YearLayoutKey, YearLayout.Grid);
        _closeToTray = !string.Equals(_store.Get(CloseToTrayKey), "false", StringComparison.Ordinal);
        _slideOutOnLeave =
            !string.Equals(_store.Get(SlideOutOnLeaveKey), "false", StringComparison.Ordinal);
        _minWidth = ReadNumber(MinWidthKey, DefaultMinWidth, LowestMinWidth, HighestMinWidth);
        _hasSlimShare = double.TryParse(_store.Get(SlimShareKey), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var share);
        _slimShare = _hasSlimShare ? Math.Clamp(share, 0.2, 0.8) : DefaultSlimShare;
        _countInCalendarDays = string.Equals(_store.Get(CountInCalendarDaysKey), "true", StringComparison.Ordinal);
        _hourHeight = ReadNumber(HourHeightKey, 0, 0, 200);
        _feedUrl = _store.Get(FeedUrlKey) ?? string.Empty;
        _feedAuto = !string.Equals(_store.Get(FeedAutoKey), "false", StringComparison.Ordinal);
        _notifyEnabled = string.Equals(_store.Get(NotifyKey), "true", StringComparison.Ordinal);
        _notifyLeadMinutes = ReadNumber(NotifyLeadKey, DefaultLeadMinutes, 0, 24 * 60);
        _summaryEnabled = string.Equals(_store.Get(SummaryKey), "true", StringComparison.Ordinal);
        _notifySound = !string.Equals(_store.Get(NotifySoundKey), "false", StringComparison.Ordinal);
        _summaryTime = TimeOnly.TryParseExact(
            _store.Get(SummaryTimeKey), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : DefaultSummaryTime;
        _checkForUpdateOnStartup =
            !string.Equals(_store.Get(CheckForUpdateOnStartupKey), "false", StringComparison.Ordinal);
        _dayStartHour = ReadHour(DayStartKey, DefaultDayStartHour);
        _dayEndHour = ReadHour(DayEndKey, DefaultDayEndHour);

        // 前後が入れ替わっていたら既定に戻す。時間軸の高さが負になる
        if (_dayEndHour <= _dayStartHour)
        {
            _dayStartHour = DefaultDayStartHour;
            _dayEndHour = DefaultDayEndHour;
        }
    }

    /// <summary>どれかが変わったときに呼ばれる。画面はこれを見て組み直す。</summary>
    public event EventHandler? Changed;

    /// <summary>配色。</summary>
    public ThemeChoice Theme
    {
        get => _theme;
        set => Write(ref _theme, value, ThemeKey);
    }

    /// <summary>週の始まりの曜日。月ビューの列の並びとミニ月暦に効く。</summary>
    public DayOfWeek WeekStart
    {
        get => _weekStart;
        set => Write(ref _weekStart, value, WeekStartKey);
    }

    /// <summary>週ビュー・日ビューの時間軸の上端。</summary>
    public int DayStartHour
    {
        get => _dayStartHour;
        set
        {
            var hour = Math.Clamp(value, 0, 23);

            // 上端を下端より後ろに置けない。押し出す形で下端も動かす
            if (hour >= _dayEndHour) Write(ref _dayEndHour, Math.Min(hour + 1, 24), DayEndKey);

            Write(ref _dayStartHour, hour, DayStartKey);
        }
    }

    /// <summary>週ビュー・日ビューの時間軸の下端。</summary>
    public int DayEndHour
    {
        get => _dayEndHour;
        set
        {
            var hour = Math.Clamp(value, 1, 24);

            if (hour <= _dayStartHour) Write(ref _dayStartHour, Math.Max(hour - 1, 0), DayStartKey);

            Write(ref _dayEndHour, hour, DayEndKey);
        }
    }

    /// <summary>起動したときに開くビュー。</summary>
    public CalendarView StartupView
    {
        get => _startupView;
        set => Write(ref _startupView, value, StartupViewKey);
    }

    /// <summary>
    /// 日数を暦日で数えるか。
    /// <para>
    /// 既定は実働日。立てると、期限までの残り・遅れ・済んだタスクの結果を、
    /// 土日や休みも含めた暦のとおりに数える。
    /// </para>
    /// <para><b>実働日計算のほうは、この設定に関わらず常に実働日で数える。</b></para>
    /// </summary>
    public bool CountInCalendarDays
    {
        get => _countInCalendarDays;
        set
        {
            if (_countInCalendarDays == value) return;

            _countInCalendarDays = value;
            _store.Set(CountInCalendarDaysKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 週ビュー・日ビューの1時間ぶんの高さ（px）。
    /// <para>
    /// 0 は「自動」で、選んだ時間帯が画面の縦にちょうど収まるように決める。
    /// 数字を入れるとその高さで固定し、入りきらないぶんはスクロールになる。
    /// </para>
    /// </summary>
    public int HourHeight
    {
        get => _hourHeight;
        set
        {
            var clamped = value <= 0 ? 0 : Math.Clamp(value, 20, 200);
            if (_hourHeight == clamped) return;

            _hourHeight = clamped;
            _store.Set(HourHeightKey, clamped.ToString(CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 実働日データの配信元（feed.json の URL）。
    /// <para>
    /// 配布の Excel を全員に配る代わりに、1か所に置いたファイルを各端末が取りに行く。
    /// <b>https のみ。</b>途中で書き換えられたものを取り込むわけにいかない。
    /// </para>
    /// </summary>
    public string FeedUrl
    {
        get => _feedUrl;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.Equals(_feedUrl, trimmed, StringComparison.Ordinal)) return;

            // https でない URL や、URL として解釈できない文字列は保存しない。以後の取得が
            // 毎回失敗するだけでなく、起動時の自動取得が黙って失敗して「今日は確認済み」に
            // なってしまう。空（取りに行かない）だけは特別に許す
            if (trimmed.Length > 0 && !IsUsableFeedUrl(trimmed)) return;

            _feedUrl = trimmed;
            _store.Set(FeedUrlKey, trimmed);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>配信元から自動で取りに行くか。1日に1回まで。</summary>
    public bool FeedAuto
    {
        get => _feedAuto;
        set
        {
            if (_feedAuto == value) return;

            _feedAuto = value;
            _store.Set(FeedAutoKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>配信元を最後に見に行った日。1日1回に抑えるために控える。</summary>
    public DateOnly? FeedCheckedOn
    {
        get => DateOnly.TryParseExact(
            _store.Get(FeedCheckedKey), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
        set => _store.Set(
            FeedCheckedKey, value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>配信元として受け取れる URL か。https だけを通す。</summary>
    public static bool IsUsableFeedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>事前通知の既定。予定の10分前。</summary>
    public const int DefaultLeadMinutes = 10;

    /// <summary>日次サマリーの既定の時刻。</summary>
    public static readonly TimeOnly DefaultSummaryTime = new(8, 0);

    /// <summary>
    /// 予定の前に知らせるか。
    /// <para>既定は出さない。要らない人に出し続けるより、要る人に入れてもらう。</para>
    /// </summary>
    public bool NotifyEnabled
    {
        get => _notifyEnabled;
        set
        {
            if (_notifyEnabled == value) return;

            _notifyEnabled = value;
            _store.Set(NotifyKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>何分前に知らせるか。</summary>
    public int NotifyLeadMinutes
    {
        get => _notifyLeadMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0, 24 * 60);
            if (_notifyLeadMinutes == clamped) return;

            _notifyLeadMinutes = clamped;
            _store.Set(NotifyLeadKey, clamped.ToString(CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// ウィンドウのときに出していたパネル。
    /// <para>
    /// 画面端に寄せたときとは別に覚える。広いウィンドウでは3つとも出し、細い帯では
    /// 予定だけ、という使い分けがふつうなので、行き来のたびに直すのは手間になる。
    /// </para>
    /// </summary>
    public string WindowPanes
    {
        get => _store.Get(WindowPanesKey) ?? string.Empty;
        set => _store.Set(WindowPanesKey, value);
    }

    /// <summary>画面端に寄せている（スライド・固定）ときに出していたパネル。</summary>
    public string EdgePanes
    {
        get => _store.Get(EdgePanesKey) ?? string.Empty;
        set => _store.Set(EdgePanesKey, value);
    }

    /// <summary>
    /// 工程逆算（実働日計算パネル）の、名前付きオフセット列。
    /// <para>
    /// 「仕様期限 −12実働日」「1次GO −9」のような節目の並びを複数セット持てる。
    /// 製品や区分でリードタイム構造が違う場合に使い分ける（要件書 4.2）。
    /// </para>
    /// <para>
    /// 値そのものはキャッシュしない。<see cref="WindowPanes"/> と同じく、書くたびに
    /// 設定の表へ直接入れて読み直す（構造のある値は JSON、という既存の約束に従う）。
    /// </para>
    /// </summary>
    public IReadOnlyList<WorkdayOffsetPlan> WorkdayOffsetPlans
    {
        get => WorkdayOffsetPlanStore.Read(_store.Get(WorkdayOffsetPlansKey));
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _store.Set(WorkdayOffsetPlansKey, WorkdayOffsetPlanStore.Write(value));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>最後に選んでいた工程逆算のセット。次に開いたときも同じものを出す。</summary>
    public string? SelectedWorkdayOffsetPlanId
    {
        get => _store.Get(WorkdaySelectedPlanKey) is { Length: > 0 } id ? id : null;
        set
        {
            if (string.Equals(SelectedWorkdayOffsetPlanId, value, StringComparison.Ordinal)) return;

            _store.Set(WorkdaySelectedPlanKey, value ?? string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// スリムパネルで、カレンダーに割く高さの割合。
    /// <para>
    /// 仕切りをつまんで変えたぶんを覚える。カレンダーを広く見たい人と、予定の一覧を
    /// 長く出したい人がいる。0.2〜0.8 の範囲に収める。
    /// </para>
    /// </summary>
    public double SlimCalendarShare
    {
        get => _slimShare;
        set
        {
            var share = Math.Clamp(value, 0.2, 0.8);

            if (_hasSlimShare && Math.Abs(_slimShare - share) < 0.005) return;

            _slimShare = share;
            _hasSlimShare = true;
            _store.Set(SlimShareKey, share.ToString("R", CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private double _slimShare = DefaultSlimShare;

    /// <summary>
    /// <see cref="SlimCalendarShare"/> をつまんで変え、控えたことがあるか。
    /// <para>
    /// まだ一度も変えていない（＝設定に値が無い）なら、既定の固定割合ではなく
    /// 月カレンダーの中身が要る高さを初期値にする（項目5）。<c>SidebarLayout</c>
    /// が起動時にこれを見て、初回だけ中身基準の高さで組む。
    /// </para>
    /// </summary>
    public bool HasSlimCalendarShare => _hasSlimShare;

    private bool _hasSlimShare;

    /// <summary>
    /// 一度もつまんで変えていないときの、保険としての既定値。
    /// <para>
    /// 実際の初期表示は <see cref="HasSlimCalendarShare"/> が false のあいだ
    /// <c>SidebarLayout.xaml</c> の <c>CalendarRow</c> を <c>Height="Auto"</c>
    /// のまま使い、月カレンダーの中身（曜日の見出し＋6週ぶん）が要る高さに
    /// 任せる（項目5）。これのほうが、固定の割合よりも画面の高さが変わったときに
    /// 「余白が余る」「6週目が切れる」を起こさない。
    /// </para>
    /// <para>
    /// この定数が使われるのは、その仕組みを経ずに <see cref="SlimCalendarShare"/>
    /// を読む場面（<c>_settings</c> が null のときの <c>MainViewModel</c> の
    /// フォールバックなど）だけ。実測（スリムパネル幅244px・高さ約1030pxで、
    /// 曜日の見出し＋6週ぶんが画面の約28〜30%だった）に合わせ、以前の 0.6 から
    /// 0.3 へ下げてある。
    /// </para>
    /// </summary>
    public const double DefaultSlimShare = 0.3;

    /// <summary>
    /// いちばん細くできる幅の既定。
    /// <para>実機で詰めてみて、月のマスと予定の行がどちらも読める下限がこのあたり。</para>
    /// </summary>
    public const int DefaultMinWidth = 220;

    /// <summary>そこまで下げられる下限。これ以下は日付も読めない。</summary>
    public const int LowestMinWidth = 160;

    /// <summary>そこまで上げられる上限。</summary>
    public const int HighestMinWidth = 640;

    /// <summary>
    /// 窓をいちばん細くできる幅。
    /// <para>
    /// 帯としてどこまで詰めたいかは使う人によるので、決め打ちにしない。細くすれば
    /// 場所を取らないが、中身は順に切れていく。
    /// </para>
    /// </summary>
    public int MinWidth
    {
        get => _minWidth;
        set
        {
            var width = Math.Clamp(value, LowestMinWidth, HighestMinWidth);

            if (_minWidth == width) return;

            _minWidth = width;
            _store.Set(MinWidthKey, width.ToString(CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 知らせるときに音を鳴らすか。既定は鳴らす。
    /// <para>画面を見ていないときに黙って出しても気づけない。</para>
    /// </summary>
    public bool NotifySound
    {
        get => _notifySound;
        set
        {
            if (_notifySound == value) return;

            _notifySound = value;
            _store.Set(NotifySoundKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>朝にその日の予定をまとめて知らせるか。</summary>
    public bool SummaryEnabled
    {
        get => _summaryEnabled;
        set
        {
            if (_summaryEnabled == value) return;

            _summaryEnabled = value;
            _store.Set(SummaryKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>まとめて知らせる時刻。</summary>
    public TimeOnly SummaryTime
    {
        get => _summaryTime;
        set
        {
            if (_summaryTime == value) return;

            _summaryTime = value;
            _store.Set(SummaryTimeKey, value.ToString("HH:mm", CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 新しい予定を入れる先のカレンダー。
    /// <para>
    /// 左の一覧から選ぶ。決めていなければ、一覧の先頭（「inaCalendar」を除く）になる。
    /// </para>
    /// </summary>
    public string? DefaultCalendarId
    {
        get => _store.Get(DefaultCalendarKey) is { Length: > 0 } id ? id : null;
        set
        {
            if (string.Equals(DefaultCalendarId, value, StringComparison.Ordinal)) return;

            _store.Set(DefaultCalendarKey, value ?? string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 新しいタスクを入れる先のタスクリスト。
    /// <para>
    /// 左の一覧から選ぶ。決めていなければ、呼び出し側（<c>SourceListsViewModel.DefaultTaskList</c>）が
    /// Google のリストがあればその先頭、無ければローカルの先頭を選ぶ
    /// （<c>local:</c> は同期対象外なので、決めていないのにローカル固定にしない）。
    /// </para>
    /// </summary>
    public string? DefaultTaskListId
    {
        get => _store.Get(DefaultTaskListKey) is { Length: > 0 } id ? id : null;
        set
        {
            if (string.Equals(DefaultTaskListId, value, StringComparison.Ordinal)) return;

            _store.Set(DefaultTaskListKey, value ?? string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 年ビューの出し方。
    /// <para>
    /// <b>設定画面には出さない。</b>年ビューを見ているときにしか関係しない選び方なので、
    /// 切り替えは年ビューの中のボタンで行う（要件書 5.1）。ここに置くのは、
    /// 次の起動でも同じ形で出すため。
    /// </para>
    /// </summary>
    public YearLayout YearLayout
    {
        get => _yearLayout;
        set => Write(ref _yearLayout, value, YearLayoutKey);
    }

    /// <summary>
    /// 閉じるボタンでトレイに入れるか。
    /// <para>
    /// 既定は入れる（要件書 7.4）。裏で通知を出し続けるため、閉じても終わらせない。
    /// ただし「閉じたら終わってほしい」という人もいるので選べるようにする。
    /// </para>
    /// <para>切ったときは、閉じるボタンでそのまま終わる。トレイのアイコンは出したまま。</para>
    /// </summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray == value) return;

            _closeToTray = value;
            _store.Set(CloseToTrayKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// スライドから、カーソルが外れたら引っ込めるか。
    /// <para>
    /// 既定は引っ込める。用があるときだけ出てくるのがスライドの形で、出したまま
    /// にしたければピンで留める。
    /// </para>
    /// <para>
    /// 切ったときは、他のウィンドウを触るまで出したままにする。カーソルを外に
    /// 出しながら見比べたい、という使い方のため。
    /// </para>
    /// </summary>
    public bool SlideOutOnLeave
    {
        get => _slideOutOnLeave;
        set
        {
            if (_slideOutOnLeave == value) return;

            _slideOutOnLeave = value;
            _store.Set(SlideOutOnLeaveKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 起動のたびに新しい版が無いか確かめるか。
    /// <para>
    /// 既定はオン（今までどおり）。立っていると起動のたびに <c>api.github.com</c> へ
    /// アクセスする。止めたい人のための設定で、⚙メニューからの手動の確認
    /// （「更新を確認…」）はこの設定に関わらず動く。
    /// </para>
    /// </summary>
    public bool CheckForUpdateOnStartup
    {
        get => _checkForUpdateOnStartup;
        set
        {
            if (_checkForUpdateOnStartup == value) return;

            _checkForUpdateOnStartup = value;
            _store.Set(CheckForUpdateOnStartupKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>時間軸の上端。</summary>
    public TimeOnly DayStart => new(_dayStartHour, 0);

    /// <summary>
    /// 時間軸の下端。
    /// <para>24 時は <see cref="TimeOnly"/> で表せないので、23:59 に寄せる。</para>
    /// </summary>
    public TimeOnly DayEnd => _dayEndHour >= 24 ? new TimeOnly(23, 59) : new TimeOnly(_dayEndHour, 0);

    private void Write<T>(ref T field, T value, string key) where T : struct
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        field = value;
        _store.Set(key, Text(value));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Text<T>(T value) where T : struct =>
        value is int number ? number.ToString(CultureInfo.InvariantCulture) : value.ToString() ?? string.Empty;

    private T Read<T>(string key, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(_store.Get(key), ignoreCase: true, out var saved) && Enum.IsDefined(saved)
            ? saved
            : fallback;

    private int ReadNumber(string key, int fallback, int min, int max) =>
        int.TryParse(_store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved)
            ? Math.Clamp(saved, min, max)
            : fallback;

    private int ReadHour(string key, int fallback) =>
        int.TryParse(_store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved)
            ? Math.Clamp(saved, 0, 24)
            : fallback;
}
