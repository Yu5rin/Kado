using System.Globalization;
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

    public AppSettings(SettingsRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _theme = Read(ThemeKey, ThemeChoice.Auto);
        _weekStart = Read(WeekStartKey, DayOfWeek.Sunday);
        _startupView = Read(StartupViewKey, CalendarView.Month);
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
