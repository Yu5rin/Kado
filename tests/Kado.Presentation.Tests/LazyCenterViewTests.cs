using System.Reflection;
using Kado.Data.Models;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// 年ビューと一覧ビューは、表示するまで作らない。
/// <para>
/// <c>MainWindow.xaml</c> の <c>YearView</c>／<c>AgendaView</c> は Collapsed でも
/// <c>DataContext</c> のバインディングが起動時に評価される。<see cref="MainViewModel.Year"/>／
/// <see cref="MainViewModel.Agenda"/> に結んでいたので、遅延にしたはずの2つ
/// （年は12か月、一覧は数年ぶんを組み立てる）が起動のたびに作られていた。画面は
/// <see cref="MainViewModel.YearForView"/>／<see cref="MainViewModel.AgendaForView"/> に結び、
/// 表示するまで null を受け取る。
/// </para>
/// </summary>
public class LazyCenterViewTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static MainViewModel Create(TestWorkspace test, AppSettings? settings = null) =>
        new(test.Workspace, D(2026, 9, 24), settings: settings);

    /// <summary>作ったかどうかは、公開の口（読むと作ってしまう）ではなく控えの欄で見る。</summary>
    private static bool IsBuilt(MainViewModel vm, string field) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm) is not null;

    [Fact]
    public void 起動直後は年も一覧も作られず画面へはnullを渡す()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        Assert.Null(vm.YearForView);
        Assert.Null(vm.AgendaForView);
        Assert.False(IsBuilt(vm, "_year"));
        Assert.False(IsBuilt(vm, "_agenda"));
    }

    [Fact]
    public void 設定を渡して起動しても作られない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var vm = Create(test, settings);

        Assert.Null(vm.YearForView);
        Assert.Null(vm.AgendaForView);
        Assert.False(IsBuilt(vm, "_year"));
        Assert.False(IsBuilt(vm, "_agenda"));
    }

    [Fact]
    public void 月週日の操作や日付の移動では作られない()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.SelectedDate = D(2026, 11, 5);
        vm.CurrentView = CalendarView.Week;
        vm.CurrentView = CalendarView.Day;
        vm.CurrentView = CalendarView.Month;
        vm.NextCommand.Execute(null);
        vm.TodayCommand.Execute(null);

        // 予定の追加・日付が変わる・設定の変更（ビューの組み直し）でも触らない
        test.Workspace.AddEvent(new CalendarEvent { Id = "e1", Title = "会議", Date = D(2026, 9, 24) });
        vm.Today = D(2026, 9, 25);

        Assert.Null(vm.YearForView);
        Assert.Null(vm.AgendaForView);
        Assert.False(IsBuilt(vm, "_year"));
        Assert.False(IsBuilt(vm, "_agenda"));
    }

    [Fact]
    public void 設定でビューを組み直しても作られない()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings);
        var vm = Create(test, settings);

        // 週の始まりが変わると、月・週・日を組み直す（RebuildViews）
        settings.WeekStart = settings.WeekStart == DayOfWeek.Monday ? DayOfWeek.Sunday : DayOfWeek.Monday;

        Assert.False(IsBuilt(vm, "_year"));
        Assert.False(IsBuilt(vm, "_agenda"));
        Assert.Null(vm.YearForView);
        Assert.Null(vm.AgendaForView);
    }

    [Fact]
    public void 年に切り替えると年だけが作られて通知される()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.CurrentView = CalendarView.Year;

        Assert.Contains(nameof(MainViewModel.YearForView), changed);
        Assert.NotNull(vm.YearForView);
        Assert.Same(vm.Year, vm.YearForView);
        Assert.Equal(2026, vm.YearForView!.FiscalYear);

        // 一覧は触っていない
        Assert.Null(vm.AgendaForView);
        Assert.False(IsBuilt(vm, "_agenda"));
    }

    [Fact]
    public void 一覧に切り替えると一覧だけが作られて通知される()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.CurrentView = CalendarView.Agenda;

        Assert.Contains(nameof(MainViewModel.AgendaForView), changed);
        Assert.NotNull(vm.AgendaForView);
        Assert.Same(vm.Agenda, vm.AgendaForView);

        Assert.Null(vm.YearForView);
        Assert.False(IsBuilt(vm, "_year"));
    }

    [Fact]
    public void 一度作ったあとは別のビューへ移っても同じ実体を渡し続ける()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.CurrentView = CalendarView.Year;
        var year = vm.YearForView;

        vm.CurrentView = CalendarView.Month;

        // 出していないあいだも、画面が持つ実体を取り上げない（年度の選びや表示形式が残る）
        Assert.Same(year, vm.YearForView);

        vm.CurrentView = CalendarView.Year;
        Assert.Same(year, vm.YearForView);
    }

    [Fact]
    public void 表示していたビューを作り直したら新しい実体を通知する()
    {
        using var test = TestWorkspace.Create();
        var vm = Create(test);

        vm.CurrentView = CalendarView.Year;
        vm.CurrentView = CalendarView.Agenda;
        var year = vm.YearForView;
        var agenda = vm.AgendaForView;

        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        // 日付が変わると、「今日」を持つ年・一覧は作り直す
        vm.Today = D(2026, 9, 25);

        Assert.Contains(nameof(MainViewModel.YearForView), changed);
        Assert.Contains(nameof(MainViewModel.AgendaForView), changed);
        Assert.NotSame(year, vm.YearForView);
        Assert.NotSame(agenda, vm.AgendaForView);
    }

    [Fact]
    public void 起動時のビューが年なら最初から渡す()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(test.Workspace.Settings) { StartupView = CalendarView.Year };
        var vm = Create(test, settings);

        Assert.NotNull(vm.YearForView);
        Assert.Null(vm.AgendaForView);
    }
}
