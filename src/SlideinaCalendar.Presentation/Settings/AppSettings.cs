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

    public AppSettings(SettingsRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _theme = Read(ThemeKey, ThemeChoice.Auto);
        _weekStart = Read(WeekStartKey, DayOfWeek.Sunday);
        _startupView = Read(StartupViewKey, CalendarView.Month);
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

    private int ReadHour(string key, int fallback) =>
        int.TryParse(_store.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved)
            ? Math.Clamp(saved, 0, 24)
            : fallback;
}
