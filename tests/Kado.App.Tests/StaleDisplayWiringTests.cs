using System.Text.RegularExpressions;

namespace Kado.App.Tests;

/// <summary>
/// 何日も動き続けるあいだに、古いまま残る表示・状態の直しの配線を、アプリを起動せずに検査する。
/// <para>
/// App プロジェクトは <c>net10.0-windows</c> で、この <c>net10.0</c> のテストからは参照できない
/// （<see cref="ResilienceWiringTests"/> と同じ理由）。実機でしか確かめられない配線は、直した書き方が
/// 実在するか、戻されていないかをソースで見張る。判断そのものは <c>Kado.Presentation.Tests</c> で試験している。
/// </para>
/// </summary>
public class StaleDisplayWiringTests
{
    private static string AppDirectory { get; } = Locate();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string App { get; } = Read("App.xaml.cs");

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} が見つかりません。");

        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{end} が見つかりません。");

        return source[from..to];
    }

    // ------------------------------------------------------------------
    // 項目4: タイムゾーンを変えても古いゾーンのまま動く
    // ------------------------------------------------------------------

    [Fact]
    public void 時計の変更を受けたら画面のスレッドでキャッシュを捨ててから読み直す()
    {
        var watch = Slice(App, "private void WatchForResume", "/// <summary>トレイのメニュー");

        // static の SystemEvents.TimeChanged を受けている
        Assert.Contains("SystemEvents.TimeChanged += OnTimeChanged", watch, StringComparison.Ordinal);

        // 画面のスレッドの中（カルチャのキャッシュは呼んだスレッドのものに効く）で、
        // 捨ててから時刻と表示を読み直す
        var dispatch = Slice(watch, "Dispatcher.BeginInvoke(() =>", "// 日をまたいで眠っていたら");
        var refresh = dispatch.IndexOf("ClockCaches.Refresh()", StringComparison.Ordinal);
        var clock = dispatch.IndexOf("main.OnClockChanged(", StringComparison.Ordinal);

        Assert.True(refresh >= 0, "ClockCaches.Refresh() を呼ぶこと");
        Assert.True(clock > refresh, "キャッシュを捨てたあとに OnClockChanged で読み直すこと");
        Assert.Contains("ClockChangeRules.ShouldRefreshCaches(change)", dispatch, StringComparison.Ordinal);
    }

    [Fact]
    public void ClockCachesはタイムゾーンとカルチャの両方を捨てる()
    {
        var caches = File.ReadAllText(Path.Combine(AppDirectory, "..", "Kado.Presentation", "Infrastructure", "ClockChange.cs"));

        Assert.Contains("TimeZoneInfo.ClearCachedData()", caches, StringComparison.Ordinal);
        Assert.Contains("CultureInfo.CurrentCulture.ClearCachedData()", caches, StringComparison.Ordinal);
    }

    [Fact]
    public void 作った時点のローカルのタイムゾーンを握り続けない()
    {
        var query = File.ReadAllText(Path.Combine(AppDirectory, "..", "Kado.Data", "Repositories", "ScheduleQuery.cs"));

        // 欄に入れず、毎回 TimeZoneInfo.Local を見る
        Assert.DoesNotContain("_timeZone = timeZone ?? TimeZoneInfo.Local", query, StringComparison.Ordinal);
        Assert.Contains("timeZone ?? TimeZoneInfo.Local", query, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目5: 週・日の時間軸が画面に合わない
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Views/WeekView.xaml.cs", "Views/WeekView.xaml")]
    [InlineData("Views/DayView.xaml.cs", "Views/DayView.xaml")]
    public void 週と日のビューは実体を受け取ったときも時間軸の高さを測り直す(string code, string xaml)
    {
        var source = Read(code);

        Assert.Contains("DataContextChanged += (_, _) => MeasureAgain();", source, StringComparison.Ordinal);
        Assert.Contains("Timeline.ActualHeight", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Timeline\"", Read(xaml), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目9: テーマ
    // ------------------------------------------------------------------

    [Fact]
    public void ブラシを辞書から引くコンバーターには配色の目印が付いている()
    {
        var source = Read("Converters/Converters.cs");

        // クラスごとに切り分けて、TryFindResource を呼んでいるのに目印の無いものを探す
        var classes = Regex.Matches(
            source, @"public sealed class (\w+) : ([^\r\n{]+)\r?\n\{(.*?)\r?\n\}", RegexOptions.Singleline);

        Assert.NotEmpty(classes);

        var missing = classes
            .Where(m => m.Groups[3].Value.Contains("TryFindResource", StringComparison.Ordinal)
                || m.Groups[3].Value.Contains("ThemeResources.Find", StringComparison.Ordinal))
            .Where(m => !m.Groups[2].Value.Contains("IThemeSensitiveConverter", StringComparison.Ordinal))
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "TryFindResource で色を引くのに IThemeSensitiveConverter が無い: " + string.Join(", ", missing));
    }

    [Fact]
    public void 配色を当て直したら目印の付いたバインドを結び直す()
    {
        var manager = Read("Themes/ThemeManager.cs");
        var refresh = Read("Themes/ThemeBindingRefresh.cs");

        Assert.Contains("ThemeBindingRefresh.RefreshAll()", manager, StringComparison.Ordinal);
        Assert.Contains("IThemeSensitiveConverter", refresh, StringComparison.Ordinal);
        Assert.Contains("UpdateTarget()", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Windowsの明暗の切り替えを受けて自動のときだけ当て直し終了時に外す()
    {
        var watch = Slice(App, "private void WatchSystemTheme", "/// <summary>\n    /// スリープから戻ったとき");

        Assert.Contains("SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged", watch, StringComparison.Ordinal);
        Assert.Contains("SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged", watch, StringComparison.Ordinal);
        Assert.Contains("UserPreferenceCategory.General", watch, StringComparison.Ordinal);
        Assert.Contains("UserPreferenceCategory.Color", watch, StringComparison.Ordinal);

        // 通知は UI のスレッドには来ない。渡し直してから触る
        Assert.Contains("Dispatcher.BeginInvoke", watch, StringComparison.Ordinal);
        Assert.Contains("ThemeManager.ReapplyIfSystemChanged()", watch, StringComparison.Ordinal);
        Assert.Contains("WatchSystemTheme();", App, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 項目15: 日付またぎで作り直した一覧のスクロール
    // ------------------------------------------------------------------

    [Fact]
    public void 一覧は新しい実体を受け取ったとき控えてある位置合わせを行う()
    {
        var view = Read("Views/AgendaView.xaml.cs");
        var changed = Slice(view, "DataContextChanged += (_, args) =>", "// 開いたときは");

        Assert.Contains("ScrollRequested += OnScrollRequested", changed, StringComparison.Ordinal);
        Assert.Contains("ScrollToPendingOrToday()", changed, StringComparison.Ordinal);
        Assert.Contains("TakePendingScroll()", view, StringComparison.Ordinal);
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Kado.App");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Kado.App が見つかりません。");
    }
}
