using System.Globalization;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>設定の選択肢。値と、画面に出す名前の組。</summary>
/// <typeparam name="T">設定の値の型。</typeparam>
public sealed record SettingChoice<T>(T Value, string Label)
{
    /// <summary>文字にするときは名前を出す。型の名前がそのまま画面に出るのを防ぐ。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 設定画面。
/// <para>
/// <b>保存ボタンは置かない。</b>変えたその場で効き、そのまま保存される。設定は
/// 数が少なく、どれも取り返しがつくので、確定の手数を挟む意味が薄い。
/// </para>
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IStartupRegistration _startup;

    private bool _runAtLogon;
    private string? _message;

    public SettingsViewModel(AppSettings settings, IStartupRegistration? startup = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startup = startup ?? NullStartupRegistration.Instance;
        _runAtLogon = _startup.IsSupported && _startup.IsEnabled;
    }

    /// <summary>配色の選択肢。</summary>
    public IReadOnlyList<SettingChoice<ThemeChoice>> Themes { get; } =
    [
        new(ThemeChoice.Auto, "Windows に合わせる"),
        new(ThemeChoice.Light, "ライト"),
        new(ThemeChoice.Dark, "ダーク"),
        new(ThemeChoice.Night, "夜間（ダークより明るく、目にやさしい）"),
    ];

    /// <summary>週の始まりの選択肢。</summary>
    public IReadOnlyList<SettingChoice<DayOfWeek>> WeekStarts { get; } =
    [
        new(DayOfWeek.Sunday, "日曜"),
        new(DayOfWeek.Monday, "月曜"),
        new(DayOfWeek.Saturday, "土曜"),
    ];

    /// <summary>起動したときに開くビューの選択肢。</summary>
    public IReadOnlyList<SettingChoice<CalendarView>> StartupViews { get; } =
    [
        new(CalendarView.Month, "月"),
        new(CalendarView.Week, "週"),
        new(CalendarView.Day, "日"),
    ];

    /// <summary>時間軸の上端に選べる時刻。</summary>
    public IReadOnlyList<SettingChoice<int>> StartHours { get; } = Hours(0, 23);

    /// <summary>時間軸の下端に選べる時刻。</summary>
    public IReadOnlyList<SettingChoice<int>> EndHours { get; } = Hours(1, 24);

    /// <summary>1時間の高さの選び方。</summary>
    public IReadOnlyList<SettingChoice<int>> HourHeights { get; } =
    [
        new(0, "自動（縦いっぱいに割り付ける）"),
        new(28, "28px（詰めて出す）"),
        new(36, "36px"),
        new(44, "44px"),
        new(56, "56px（ゆったり）"),
    ];

    /// <summary>1時間の高さ。0 なら画面に合わせる。</summary>
    public int HourHeight
    {
        get => _settings.HourHeight;
        set
        {
            if (_settings.HourHeight == value) return;

            _settings.HourHeight = value;
            Raise();
        }
    }

    public ThemeChoice Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;

            _settings.Theme = value;
            Raise();
        }
    }

    public DayOfWeek WeekStart
    {
        get => _settings.WeekStart;
        set
        {
            if (_settings.WeekStart == value) return;

            _settings.WeekStart = value;
            Raise();
        }
    }

    public int DayStartHour
    {
        get => _settings.DayStartHour;
        set
        {
            if (_settings.DayStartHour == value) return;

            _settings.DayStartHour = value;

            // 下端を押し出していることがある。両方とも出し直す
            Raise(nameof(DayStartHour), nameof(DayEndHour));
        }
    }

    public int DayEndHour
    {
        get => _settings.DayEndHour;
        set
        {
            if (_settings.DayEndHour == value) return;

            _settings.DayEndHour = value;
            Raise(nameof(DayStartHour), nameof(DayEndHour));
        }
    }

    /// <summary>日数の数え方。実働日か暦日か。</summary>
    public IReadOnlyList<SettingChoice<bool>> DayCounts { get; } =
    [
        new(false, "実働日で数える"),
        new(true, "暦日で数える"),
    ];

    /// <summary>暦日で数えるか。</summary>
    public bool CountInCalendarDays
    {
        get => _settings.CountInCalendarDays;
        set
        {
            if (_settings.CountInCalendarDays == value) return;

            _settings.CountInCalendarDays = value;
            Raise();
        }
    }

    public CalendarView StartupView
    {
        get => _settings.StartupView;
        set
        {
            if (_settings.StartupView == value) return;

            _settings.StartupView = value;
            Raise();
        }
    }

    /// <summary>自動起動を出せるか。扱えない環境では欄ごと隠す。</summary>
    public bool CanRunAtLogon => _startup.IsSupported;

    /// <summary>Windows にログオンしたときに起動するか。</summary>
    public bool RunAtLogon
    {
        get => _runAtLogon;
        set
        {
            if (_runAtLogon == value) return;

            if (!_startup.SetEnabled(value))
            {
                // 書けなかったことを黙らせない。チェックだけ入って効かないのが最悪
                Message = "自動起動の設定を書き込めませんでした。";
                Raise(nameof(RunAtLogon));
                return;
            }

            _runAtLogon = value;
            Message = null;
            Raise();
        }
    }

    /// <summary>うまくいかなかったときの断り書き。</summary>
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    private static SettingChoice<int>[] Hours(int from, int to) =>
        Enumerable.Range(from, to - from + 1)
            .Select(h => new SettingChoice<int>(h, $"{h.ToString(CultureInfo.InvariantCulture)}:00"))
            .ToArray();
}
