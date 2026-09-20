using SlideinaCalendar.Presentation.Settings;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>自動起動を扱えない環境の代わり。書き込みの成否を決められる。</summary>
internal sealed class FakeStartup(bool supported = true, bool writable = true) : IStartupRegistration
{
    public bool IsSupported => supported;

    public bool IsEnabled { get; private set; }

    public bool SetEnabled(bool enabled)
    {
        if (!writable) return false;

        IsEnabled = enabled;
        return true;
    }
}

public class SettingsTests
{
    [Fact]
    public void 設定は保存されて次に開いたときも残る()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);

        settings.Theme = ThemeChoice.Dark;
        settings.WeekStart = DayOfWeek.Monday;
        settings.DayStartHour = 6;
        settings.DayEndHour = 22;
        settings.StartupView = CalendarView.Week;

        var next = new AppSettings(test.Workspace.Settings);

        Assert.Equal(ThemeChoice.Dark, next.Theme);
        Assert.Equal(DayOfWeek.Monday, next.WeekStart);
        Assert.Equal(6, next.DayStartHour);
        Assert.Equal(22, next.DayEndHour);
        Assert.Equal(CalendarView.Week, next.StartupView);
    }

    [Fact]
    public void 何も保存していなければ既定()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);

        Assert.Equal(ThemeChoice.Auto, settings.Theme);
        Assert.Equal(DayOfWeek.Sunday, settings.WeekStart);
        Assert.Equal(AppSettings.DefaultDayStartHour, settings.DayStartHour);
        Assert.Equal(AppSettings.DefaultDayEndHour, settings.DayEndHour);
        Assert.Equal(CalendarView.Month, settings.StartupView);
    }

    [Fact]
    public void 表示時間帯は前後が入れ替わらない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);

        // 上端を下端より後ろに置こうとしたら、下端が押し出される
        settings.DayStartHour = 22;
        Assert.Equal(22, settings.DayStartHour);
        Assert.True(settings.DayEndHour > settings.DayStartHour);

        settings.DayEndHour = 5;
        Assert.Equal(5, settings.DayEndHour);
        Assert.True(settings.DayStartHour < settings.DayEndHour);
    }

    [Fact]
    public void 壊れた値が入っていても既定に戻る()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.Settings.Set("ui.theme", "むらさき");
        test.Workspace.Settings.Set("ui.day_start_hour", "あさ");

        var settings = new AppSettings(test.Workspace.Settings);

        Assert.Equal(ThemeChoice.Auto, settings.Theme);
        Assert.Equal(AppSettings.DefaultDayStartHour, settings.DayStartHour);
    }

    [Fact]
    public void 設定画面から変えると保存される()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var vm = new SettingsViewModel(settings, new FakeStartup());

        vm.WeekStart = DayOfWeek.Monday;
        vm.DayStartHour = 7;

        Assert.Equal(DayOfWeek.Monday, settings.WeekStart);
        Assert.Equal(7, settings.DayStartHour);
    }

    [Fact]
    public void 自動起動を書けなければ断りを出す()
    {
        using var test = TestWorkspace.Create();
        var vm = new SettingsViewModel(
            new AppSettings(test.Workspace.Settings), new FakeStartup(writable: false));

        vm.RunAtLogon = true;

        // 入ったように見えて効かない、が最悪。チェックは戻し、理由を出す
        Assert.False(vm.RunAtLogon);
        Assert.NotNull(vm.Message);
    }

    [Fact]
    public void 扱えない環境では自動起動を出さない()
    {
        using var test = TestWorkspace.Create();
        var vm = new SettingsViewModel(
            new AppSettings(test.Workspace.Settings), new FakeStartup(supported: false));

        Assert.False(vm.CanRunAtLogon);
    }

    [Fact]
    public void 週の始まりを変えると月ビューが組み直される()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        Assert.Equal(DayOfWeek.Sunday, main.Month.Cells[0].Date.DayOfWeek);

        settings.WeekStart = DayOfWeek.Monday;

        Assert.Equal(DayOfWeek.Monday, main.Month.Cells[0].Date.DayOfWeek);
    }

    [Fact]
    public void 表示時間帯を変えると週ビューの時間軸が変わる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        settings.DayStartHour = 0;
        settings.DayEndHour = 24;

        Assert.Equal("0:00", main.Week.HourLabels[0]);
        Assert.Equal(24, main.Week.HourLabels.Count);
    }

    [Fact]
    public void 起動時のビューが効く()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings) { StartupView = CalendarView.Day };

        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        Assert.True(main.IsDayView);
    }

    [Fact]
    public void 設定を持たない組み立て方では設定画面を開けない()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24));

        Assert.False(main.OpenSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void 設定を持っていれば設定画面を開ける()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24), editors: editors,
            settings: new AppSettings(test.Workspace.Settings));

        main.OpenSettingsCommand.Execute(null);

        Assert.NotNull(editors.LastSettings);
    }

    [Fact]
    public void 時間軸は使える高さに割り付けられる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        // 既定は一日ぶん。720px なら1時間 30px
        main.Week.ViewportHeight = 720;

        Assert.Equal(24, main.Week.HourLabels.Count);
        Assert.Equal(30, main.Week.HourHeight);
        Assert.Equal(720, main.Week.TimelineHeight);
    }

    [Fact]
    public void 時間軸は潰れるところまでは縮めない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        main.Week.ViewportHeight = 120;

        // これ以上狭くすると予定の題が読めない。入らないぶんはスクロールで見る
        Assert.Equal(TimelineBuilder.MinHourHeight, main.Week.HourHeight);
        Assert.True(main.Week.TimelineHeight > 120);
    }

    [Fact]
    public void 時間帯を狭めると1時間が高くなる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        main.Day.ViewportHeight = 600;
        var whole = main.Day.HourHeight;

        settings.DayStartHour = 8;
        settings.DayEndHour = 20;
        main.Day.ViewportHeight = 600;

        Assert.True(main.Day.HourHeight > whole);
    }

    [Fact]
    public void 今の時刻の線は高さを変えても位置が合う()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        main.Week.ViewportHeight = 720;          // 1時間 30px
        main.UpdateNow(new DateTime(2026, 9, 24, 10, 0, 0));

        Assert.True(main.Week.ShowNowLine);
        Assert.Equal(300, main.Week.NowOffset);  // 10時 × 30px

        // 高さが変われば線も動く。引き直さないと、罫線だけがずれていく
        main.Week.ViewportHeight = 1200;         // 1時間 50px
        Assert.Equal(500, main.Week.NowOffset);
    }

    [Fact]
    public void 今日を含まない週では線を出さない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        main.UpdateNow(new DateTime(2026, 9, 24, 10, 0, 0));
        Assert.True(main.Week.ShowNowLine);

        main.Week.GoToNextWeek();
        main.UpdateNow(new DateTime(2026, 9, 24, 10, 0, 0));

        Assert.False(main.Week.ShowNowLine);
    }

    [Fact]
    public void 暦日で数える設定は期限の表記に効く()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 18), settings: settings);

        // 9/18 から 9/24 まで。実働日なら1日（あいだが3連休）、暦では6日
        Assert.Equal("残り 1実働日", test.Workspace.DueFormatter
            .Format(new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 18)).Text);

        settings.CountInCalendarDays = true;

        Assert.Equal("残り 6日", test.Workspace.DueFormatter
            .Format(new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 18)).Text);

        // 画面も引き直されている
        Assert.NotNull(main.SelectedDay);
    }

    [Fact]
    public void 数え方は次に開いたときも残る()
    {
        using var test = TestWorkspace.Create();
        new AppSettings(test.Workspace.Settings) { CountInCalendarDays = true };

        Assert.True(new AppSettings(test.Workspace.Settings).CountInCalendarDays);
    }

    [Fact]
    public void 高さを決め打ちにすると画面に合わせない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings) { HourHeight = 44 };
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        main.Week.ViewportHeight = 720;

        Assert.Equal(44, main.Week.HourHeight);
        Assert.Equal(44 * 24, main.Week.TimelineHeight);
    }

    [Fact]
    public void 高さを自動に戻すと画面に合わせる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings) { HourHeight = 44 };
        var main = new MainViewModel(test.Workspace, today: new DateOnly(2026, 9, 24), settings: settings);

        settings.HourHeight = 0;
        main.Week.ViewportHeight = 720;

        Assert.Equal(30, main.Week.HourHeight);
    }

    [Fact]
    public void 夜間の配色も選べる()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings) { Theme = ThemeChoice.Night };

        Assert.Equal(ThemeChoice.Night, new AppSettings(test.Workspace.Settings).Theme);
    }

    [Theory]
    [InlineData("https://example.com/feed.json", true)]
    [InlineData("http://example.com/feed.json", false)]
    [InlineData("file:///C:/feed.json", false)]
    [InlineData("feed.json", false)]
    [InlineData("", false)]
    public void 配信元はhttpsだけ通す(string url, bool usable)
    {
        Assert.Equal(usable, AppSettings.IsUsableFeedUrl(url));
    }

    [Fact]
    public void 配信元は次に開いたときも残る()
    {
        using var test = TestWorkspace.Create();
        new AppSettings(test.Workspace.Settings) { FeedUrl = "https://example.com/feed.json" };

        Assert.Equal("https://example.com/feed.json", new AppSettings(test.Workspace.Settings).FeedUrl);
    }

    [Fact]
    public void 配信元がhttpsでなければ断る()
    {
        using var test = TestWorkspace.Create();
        var vm = new SettingsViewModel(new AppSettings(test.Workspace.Settings))
        {
            FeedUrl = "http://example.com/feed.json",
        };

        Assert.NotNull(vm.Message);
    }

    [Fact]
    public void スライドの引っ込め方は既定で入っていて切れる()
    {
        using var test = TestWorkspace.Create();

        // 用があるときだけ出てくるのがスライドの形。出したままにしたければピンで留める
        Assert.True(new AppSettings(test.Workspace.Settings).SlideOutOnLeave);

        new AppSettings(test.Workspace.Settings) { SlideOutOnLeave = false };

        Assert.False(new AppSettings(test.Workspace.Settings).SlideOutOnLeave);
    }

    [Fact]
    public void 配信元が空なら取りに行けない()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(
            test.Workspace, today: new DateOnly(2026, 9, 24),
            settings: new AppSettings(test.Workspace.Settings));

        Assert.False(main.FetchWorkingDayFeedCommand.CanExecute(null));
    }
}
