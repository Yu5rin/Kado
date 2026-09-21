using System.Globalization;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.Notifications;
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

    private readonly INotifier _notifier;

    public SettingsViewModel(
        AppSettings settings, IStartupRegistration? startup = null, INotifier? notifier = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startup = startup ?? NullStartupRegistration.Instance;
        _notifier = notifier ?? NullNotifier.Instance;
        _runAtLogon = _startup.IsSupported && _startup.IsEnabled;

        TestNotifyCommand = new Infrastructure.RelayCommand(TestNotify);
    }

    /// <summary>
    /// 試しに1つ出してみる。
    /// <para>
    /// 通知が出る場所と見え方は、実際に出してみないと分からない。設定を入れたのに
    /// 何も起きないとき、アプリ側の問題か時刻待ちかを切り分けられる。
    /// </para>
    /// </summary>
    public Infrastructure.RelayCommand TestNotifyCommand { get; }

    private void TestNotify()
    {
        if (!_notifier.IsSupported)
        {
            Message = "この環境では知らせられません";
            return;
        }

        _notifier.Notify("試しの知らせ", "この形で予定の前にお知らせします", _settings.NotifySound);
        Message = null;
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

    /// <summary>何分前に知らせるかの選択肢。</summary>
    public IReadOnlyList<SettingChoice<int>> Leads { get; } =
    [
        new(0, "予定の時刻ちょうど"),
        new(5, "5分前"),
        new(10, "10分前"),
        new(15, "15分前"),
        new(30, "30分前"),
        new(60, "1時間前"),
    ];

    /// <summary>まとめて知らせる時刻の選択肢。</summary>
    public IReadOnlyList<SettingChoice<int>> SummaryHours { get; } =
        Enumerable.Range(5, 14)
            .Select(h => new SettingChoice<int>(h, $"{h.ToString(CultureInfo.InvariantCulture)}:00"))
            .ToArray();

    /// <summary>知らせられる環境か。出せないなら欄ごと隠す。</summary>
    public bool CanNotify { get; } = true;

    /// <summary>予定の前に知らせるか。</summary>
    public bool NotifyEnabled
    {
        get => _settings.NotifyEnabled;
        set
        {
            if (_settings.NotifyEnabled == value) return;

            _settings.NotifyEnabled = value;
            Raise();
        }
    }

    /// <summary>何分前に知らせるか。</summary>
    public int NotifyLeadMinutes
    {
        get => _settings.NotifyLeadMinutes;
        set
        {
            if (_settings.NotifyLeadMinutes == value) return;

            _settings.NotifyLeadMinutes = value;
            Raise();
        }
    }

    /// <summary>
    /// 閉じるボタンでトレイに入れるか。
    /// <para>切ると、閉じるボタンでそのまま終わる。</para>
    /// </summary>
    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;

            _settings.CloseToTray = value;
            Raise();
        }
    }

    /// <summary>
    /// スライドから、カーソルが外れたら引っ込めるか。
    /// <para>切ると、他のウィンドウを触るまで出したままにする。</para>
    /// </summary>
    public bool SlideOutOnLeave
    {
        get => _settings.SlideOutOnLeave;
        set
        {
            if (_settings.SlideOutOnLeave == value) return;

            _settings.SlideOutOnLeave = value;
            Raise();
        }
    }

    /// <summary>
    /// 窓をいちばん細くできる幅。
    /// <para>
    /// 文字列として受ける。<c>TextBox</c> に <c>int</c> を直接束ねると、数字以外を
    /// 打ったとき WPF の既定の型変換エラーで元の値へ静かに戻るだけで、何も言われない。
    /// ここで <see cref="int.TryParse(string?, out int)"/> して、失敗したときは理由を出す。
    /// 全角の数字も半角に直してから読む。
    /// </para>
    /// <para>範囲外（160〜640の外）は <see cref="AppSettings.MinWidth"/> 側で丸められる。丸まったら伝える。</para>
    /// </summary>
    public string MinWidthText
    {
        get => _settings.MinWidth.ToString(CultureInfo.InvariantCulture);
        set
        {
            var normalized = NormalizeDigits(value);

            if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                Message = "いちばん細くできる幅は数字で入れてください。";
                Raise();
                return;
            }

            _settings.MinWidth = parsed;
            Message = _settings.MinWidth == parsed
                ? null
                : $"{AppSettings.LowestMinWidth}〜{AppSettings.HighestMinWidth}px の範囲に収めました（{_settings.MinWidth}px）。";

            Raise();
        }
    }

    /// <summary>全角の数字を半角に直す。「１６０」のような入力も受けたい。</summary>
    private static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        return new string(value.Select(
            c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c).ToArray());
    }

    /// <summary>知らせるときに音を鳴らすか。</summary>
    public bool NotifySound
    {
        get => _settings.NotifySound;
        set
        {
            if (_settings.NotifySound == value) return;

            _settings.NotifySound = value;
            Raise();
        }
    }

    /// <summary>朝にその日の予定をまとめて知らせるか。</summary>
    public bool SummaryEnabled
    {
        get => _settings.SummaryEnabled;
        set
        {
            if (_settings.SummaryEnabled == value) return;

            _settings.SummaryEnabled = value;
            Raise();
        }
    }

    /// <summary>まとめて知らせる時刻（時）。</summary>
    public int SummaryHour
    {
        get => _settings.SummaryTime.Hour;
        set
        {
            if (_settings.SummaryTime.Hour == value) return;

            _settings.SummaryTime = new TimeOnly(Math.Clamp(value, 0, 23), 0);
            Raise();
        }
    }

    /// <summary>
    /// 実働日データの配信元（feed.json の URL）。
    /// <para>
    /// https でない URL や、URL として解釈できない文字列は保存しない。取得が毎回
    /// 失敗するだけでなく、起動時の自動取得が黙って失敗して「今日は確認済み」に
    /// なってしまうため。空（取りに行かない）だけは特別に許す。
    /// </para>
    /// </summary>
    public string FeedUrl
    {
        get => _settings.FeedUrl;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();

            if (string.Equals(_settings.FeedUrl, trimmed, StringComparison.Ordinal))
            {
                Message = null;
                return;
            }

            if (trimmed.Length > 0 && !AppSettings.IsUsableFeedUrl(trimmed))
            {
                // 保存はしない。前の値のまま留め、理由だけ返す
                Message = "配信元は https:// で始まる URL にしてください（保存されていません）";
                Raise();
                return;
            }

            _settings.FeedUrl = trimmed;
            Message = null;
            Raise();
        }
    }

    /// <summary>配信元から自動で取りに行くか。</summary>
    public bool FeedAuto
    {
        get => _settings.FeedAuto;
        set
        {
            if (_settings.FeedAuto == value) return;

            _settings.FeedAuto = value;
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
