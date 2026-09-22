using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Notifications;
using Kado.Presentation.Settings;

namespace Kado.Presentation.ViewModels;

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

    /// <summary>
    /// カレンダーごとの通知オン・オフの一覧。
    /// <para>
    /// 左パネルの行のベルと同じ <see cref="SourceListItemViewModel"/> をそのまま渡す。
    /// 同じインスタンスなので、ここで切り替えれば左パネルにもそのまま反映される。
    /// </para>
    /// <para>
    /// <b>既定は null。</b>呼び出し側（<c>MainViewModel</c>）がカレンダー一覧への経路を
    /// まだ持っていないため、既定値付きの任意引数にしてある。渡されなければ、
    /// 設定画面の通知タブにこの節は出さない。
    /// </para>
    /// <para>
    /// <c>sync</c> 以降は、⚙メニューから設定画面へ移した項目のための配線。<c>MainViewModel</c>
    /// がすでに持っているコマンドをそのまま渡してもらう形にしてある。既定値付きの
    /// 任意引数にしてあるのは <c>calendars</c> と同じ理由で、渡されなければ画面側は
    /// 「押せない」または「節ごと出さない」で受ける（各プロパティのコメントを参照）。
    /// </para>
    /// </summary>
    public SettingsViewModel(
        AppSettings settings, IStartupRegistration? startup = null, INotifier? notifier = null,
        IReadOnlyList<SourceListItemViewModel>? calendars = null,
        SyncViewModel? sync = null,
        Infrastructure.RelayCommand? importWorkingDays = null,
        Infrastructure.RelayCommand? importLegacyBackup = null,
        Infrastructure.AsyncRelayCommand? fetchWorkingDayFeed = null,
        Infrastructure.RelayCommand? exportWorkingDayFeed = null,
        Infrastructure.RelayCommand? removeDuplicates = null,
        Infrastructure.RelayCommand? backup = null,
        Infrastructure.RelayCommand? restore = null,
        Infrastructure.RelayCommand? importGoogleClient = null,
        Infrastructure.AsyncRelayCommand? checkForUpdate = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _startup = startup ?? NullStartupRegistration.Instance;
        _notifier = notifier ?? NullNotifier.Instance;
        _runAtLogon = _startup.IsSupported && _startup.IsEnabled;

        // ベルを持つのはカレンダーだけ（タスクリストは通知の対象にしていない）
        NotifiableCalendars = calendars?.Where(c => c.HasNotifyToggle).ToArray()
            ?? Array.Empty<SourceListItemViewModel>();

        Sync = sync;
        ImportWorkingDaysCommand = importWorkingDays;
        ImportLegacyBackupCommand = importLegacyBackup;
        FetchWorkingDayFeedCommand = fetchWorkingDayFeed;
        ExportWorkingDayFeedCommand = exportWorkingDayFeed;
        RemoveDuplicatesCommand = removeDuplicates;
        BackupCommand = backup;
        RestoreCommand = restore;
        ImportGoogleClientCommand = importGoogleClient;
        CheckForUpdateCommand = checkForUpdate;

        TestNotifyCommand = new Infrastructure.RelayCommand(TestNotify);
        OpenCrashLogCommand = new Infrastructure.RelayCommand(OpenCrashLog, () => HasCrashLog);
        ClearCrashLogCommand = new Infrastructure.RelayCommand(ClearCrashLog, () => HasCrashLog);
        OpenDataFolderCommand = new Infrastructure.RelayCommand(OpenDataFolder);
        OpenRepositoryCommand = new Infrastructure.RelayCommand(OpenRepository);
    }

    /// <summary>カレンダーごとの通知オン・オフの一覧。渡されていなければ空。</summary>
    public IReadOnlyList<SourceListItemViewModel> NotifiableCalendars { get; }

    /// <summary>通知タブに、カレンダーごとの一覧の節を出すか。</summary>
    public bool HasCalendarNotifyList => NotifiableCalendars.Count > 0;

    // ------------------------------------------------------------------
    // ⚙メニューから移した項目
    //
    // どれも MainViewModel がすでに持っているコマンドをそのまま受け取って
    // 画面に出すだけ。ここで新しく処理は書かない（確認ダイアログや
    // ファイル選択は呼び出し先の MainViewModel 側にある）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 右上の同期表示と同じ <see cref="SyncViewModel"/>。
    /// <para>
    /// 「同期（Google）」節の接続状態・接続／切断ボタンに使う。<b>「今すぐ同期」は
    /// ⚙メニューに残すので、ここでは使わない。</b>
    /// </para>
    /// <para>既定は null。渡されなければ「同期（Google）」節ごと出さない（<see cref="HasSync"/>）。</para>
    /// </summary>
    public SyncViewModel? Sync { get; }

    /// <summary>「同期（Google）」節を出すか。</summary>
    public bool HasSync => Sync is not null;

    /// <summary>実働日ファイルを取り込む（Excel）。⚙メニューにもあるが、配信元 URL の隣に置くと便利なのでこちらにも出す。</summary>
    public Infrastructure.RelayCommand? ImportWorkingDaysCommand { get; }

    /// <summary>旧 inaCalendar のバックアップを取り込む（JSON）。移行時の1回きりなので、⚙メニューからはここへ移した。</summary>
    public Infrastructure.RelayCommand? ImportLegacyBackupCommand { get; }

    /// <summary>配信元から実働日を取り込む。⚙メニューにもあるが、配信元 URL の隣に置くと便利なのでこちらにも出す。</summary>
    public Infrastructure.AsyncRelayCommand? FetchWorkingDayFeedCommand { get; }

    /// <summary>実働日を配信用に書き出す（feed.json）。⚙メニューからここへ移した。</summary>
    public Infrastructure.RelayCommand? ExportWorkingDayFeedCommand { get; }

    /// <summary>重複した予定を整理する。元に戻せないので「データ」節の末尾に置く。⚙メニューからここへ移した。</summary>
    public Infrastructure.RelayCommand? RemoveDuplicatesCommand { get; }

    /// <summary>バックアップを保存する。⚙メニューからここへ移した。</summary>
    public Infrastructure.RelayCommand? BackupCommand { get; }

    /// <summary>バックアップから復元する。いまの内容が置き換わるので「データ」節の末尾に置く。⚙メニューからここへ移した。</summary>
    public Infrastructure.RelayCommand? RestoreCommand { get; }

    /// <summary>自分の Google Cloud プロジェクトのクライアント設定を使う（上級者向け）。⚙メニューの「詳細」からここへ移した。</summary>
    public Infrastructure.RelayCommand? ImportGoogleClientCommand { get; }

    /// <summary>更新を確かめる。⚙メニューから「バージョン情報」節へ移した。</summary>
    public Infrastructure.AsyncRelayCommand? CheckForUpdateCommand { get; }

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

    /// <summary>
    /// 起動のたびに新しい版が無いか確かめるか。
    /// <para>
    /// 切っても、⚙メニューからの手動の確認（「更新を確認…」）はこの設定に関わらず動く。
    /// </para>
    /// </summary>
    public bool CheckForUpdateOnStartup
    {
        get => _settings.CheckForUpdateOnStartup;
        set
        {
            if (_settings.CheckForUpdateOnStartup == value) return;

            _settings.CheckForUpdateOnStartup = value;
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

    // ------------------------------------------------------------------
    // エラーの記録（crash.log）
    //
    // App.xaml.cs が書く先と同じ場所を、こちらでも計算する。データベースと
    // 同じフォルダに置く決まりなので、CalendarDatabase.DefaultPath から求まる
    // ------------------------------------------------------------------

    /// <summary>異常終了の記録先。<c>App.xaml.cs</c> の書き出し先と同じ場所。</summary>
    private static string CrashLogPath => Path.Combine(
        Path.GetDirectoryName(Kado.Data.CalendarDatabase.DefaultPath)!, "crash.log");

    /// <summary>1つ前の世代（1MB を超えたときに繰った分）。</summary>
    private static string PreviousCrashLogPath => CrashLogPath + ".1";

    /// <summary>記録が残っているか。無ければ開く・消すボタンを押せなくする。</summary>
    public bool HasCrashLog => File.Exists(CrashLogPath) || File.Exists(PreviousCrashLogPath);

    /// <summary>エラーの記録を開く。</summary>
    public Infrastructure.RelayCommand OpenCrashLogCommand { get; }

    /// <summary>エラーの記録を消す。</summary>
    public Infrastructure.RelayCommand ClearCrashLogCommand { get; }

    private void OpenCrashLog()
    {
        try
        {
            // 既定のアプリ（ふつうはメモ帳）で開く。中身をここで読んで見せると、
            // 大きくなっていたときに画面が固まる
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(CrashLogPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or PlatformNotSupportedException)
        {
            Message = $"エラーの記録を開けませんでした（{ex.Message}）";
            Raise(nameof(Message));
        }
    }

    private void ClearCrashLog()
    {
        try
        {
            if (File.Exists(CrashLogPath)) File.Delete(CrashLogPath);
            if (File.Exists(PreviousCrashLogPath)) File.Delete(PreviousCrashLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"エラーの記録を消せませんでした（{ex.Message}）";
            Raise(nameof(Message));
            return;
        }

        Message = null;
        Raise(nameof(Message), nameof(HasCrashLog));
        OpenCrashLogCommand.RaiseCanExecuteChanged();
        ClearCrashLogCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------
    // バージョン情報
    // ------------------------------------------------------------------

    /// <summary>アプリ名。画面にそのまま出す。</summary>
    public string AppName => "Kado";

    /// <summary>使っている主なもの。決め打ちの表示なので、大きく変えたときだけ直す。</summary>
    public string TechStack => ".NET 8 ／ WPF ／ SQLite";

    /// <summary>
    /// 画面に出すバージョン。
    /// <para>
    /// <b>決め打ちにしない。</b>アセンブリの <c>AssemblyFileVersion</c> から読む。
    /// リリースワークフロー（<c>.github/workflows/release.yml</c>）がタグから
    /// <c>-p:Version=</c> で焼き込む値で、これが <c>AssemblyFileVersion</c> と
    /// <c>AssemblyInformationalVersion</c> の両方に反映される。
    /// </para>
    /// <para>
    /// タグを押さずに手動実行したときは <c>0.0.0-dev</c> が入る。ローカルでの
    /// ふつうの <c>dotnet build</c>（<c>-p:Version=</c> を渡さないとき）は
    /// SDK の既定値 <c>1.0.0</c> になる。どちらもリリースの版数ではないので
    /// 「開発版」に落とす（<see cref="FormatVersion"/>）。
    /// </para>
    /// </summary>
    public string AppVersion => FormatVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);

    /// <summary>
    /// <see cref="AppVersion"/> の中身。文字列を渡す形にして、実際のアセンブリが無い
    /// テストからも確かめられるようにしてある。
    /// </summary>
    internal static string FormatVersion(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion) || !Version.TryParse(fileVersion, out var version))
        {
            return "開発版";
        }

        // 1.0.0 は -p:Version を渡さなかったときの SDK の既定値、0.0.0 は
        // ワークフローを手動実行したときの印（0.0.0-dev）。どちらもタグから
        // 来た本物の版数ではない
        var isUnfilled = version is { Major: 1, Minor: 0, Build: 0 } or { Major: 0, Minor: 0, Build: 0 };

        return isUnfilled ? "開発版" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>リポジトリのページ。固定の URL なので定数で持つ。</summary>
    private const string RepositoryUrl = "https://github.com/Yu5rin/SlideinaCalendar";

    /// <summary>データの保存先を開く。</summary>
    public Infrastructure.RelayCommand OpenDataFolderCommand { get; }

    /// <summary>リポジトリのページをブラウザで開く。</summary>
    public Infrastructure.RelayCommand OpenRepositoryCommand { get; }

    private void OpenDataFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(Kado.Data.CalendarDatabase.DefaultPath)!;

            // 中身をアプリが持ってしまうより、使い慣れたエクスプローラーに任せる
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or PlatformNotSupportedException)
        {
            Message = $"保存先を開けませんでした（{ex.Message}）";
            Raise(nameof(Message));
        }
    }

    private void OpenRepository()
    {
        // 定数なので通信先が変わることは無いが、ShellExecute に無検証で文字列を
        // 渡さない流儀は更新の確認（ReleaseFeed.IsAllowedDownloadUrl）と揃える
        if (!IsAllowedExternalUrl(RepositoryUrl))
        {
            Message = "リポジトリのページを開けませんでした。";
            Raise(nameof(Message));
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or PlatformNotSupportedException)
        {
            Message = $"リポジトリのページを開けませんでした（{ex.Message}）";
            Raise(nameof(Message));
        }
    }

    /// <summary>https で、github.com 宛てであること。</summary>
    internal static bool IsAllowedExternalUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static SettingChoice<int>[] Hours(int from, int to) =>
        Enumerable.Range(from, to - from + 1)
            .Select(h => new SettingChoice<int>(h, $"{h.ToString(CultureInfo.InvariantCulture)}:00"))
            .ToArray();
}
