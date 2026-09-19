using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>
/// 取り込みの呼び出し口。
/// <para>
/// この口が無いと配布の Excel を読み込む手段が無く、実働日の表示も計算も
/// 動かないままになる（要件書 4.1）。
/// </para>
/// </summary>
public class ImportCommandTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>リポジトリに置いてある配布ファイルの実物。</summary>
    private static string WorkdayFile => Find("実働日サンプル.xlsx");

    private static string LegacyFile => Find("inaCalendar-backup-sample.json");

    private static (MainViewModel Vm, FakeFileDialogs Files) Create(TestWorkspace test)
    {
        var files = new FakeFileDialogs();
        return (new MainViewModel(test.Workspace, today: D(2026, 9, 24), files: files), files);
    }

    [Fact]
    public void 実働日ファイルを取り込める()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var (vm, files) = Create(test);

        Assert.False(vm.HasWorkingDayData);

        files.FileToPick = WorkdayFile;
        vm.ImportWorkingDaysCommand.Execute(null);

        Assert.Contains("実働日を取り込みました", vm.StatusMessage);
        Assert.Contains("稼働日", files.LastReport);

        // サンプルの範囲は 2023/1/5 〜 2025/11/24
        Assert.True(test.Workspace.WorkingDays.HasDataFor(D(2025, 11, 4)));
        Assert.False(test.Workspace.WorkingDays.HasDataFor(D(2026, 4, 1)));
    }

    [Fact]
    public void 取り消したら何も起きない()
    {
        using var test = TestWorkspace.Create(withWorkingDays: false);
        var (vm, files) = Create(test);

        files.FileToPick = null;
        vm.ImportWorkingDaysCommand.Execute(null);

        Assert.Null(vm.StatusMessage);
        Assert.Null(files.LastReport);
    }

    [Fact]
    public void 読めないファイルでも落ちない()
    {
        using var test = TestWorkspace.Create();
        var (vm, files) = Create(test);

        // JSON を実働日ファイルとして選ばせる
        files.FileToPick = LegacyFile;
        vm.ImportWorkingDaysCommand.Execute(null);

        Assert.Contains("取り込めませんでした", files.LastReport);
    }

    [Fact]
    public void 存在しないファイルでも落ちない()
    {
        using var test = TestWorkspace.Create();
        var (vm, files) = Create(test);

        files.FileToPick = Path.Combine(Path.GetTempPath(), "存在しない-" + Guid.NewGuid().ToString("N") + ".xlsx");
        vm.ImportWorkingDaysCommand.Execute(null);

        Assert.Contains("取り込めませんでした", files.LastReport);
    }

    [Fact]
    public void 旧データを取り込める()
    {
        using var test = TestWorkspace.Create();
        var (vm, files) = Create(test);

        files.FileToPick = LegacyFile;
        vm.ImportLegacyBackupCommand.Execute(null);

        Assert.Contains("旧データを取り込みました", vm.StatusMessage);
        Assert.Contains("ToDo から変換", vm.StatusMessage);
    }

    [Fact]
    public void 旧データの取り込みは先に断ってから()
    {
        using var test = TestWorkspace.Create();
        var (vm, files) = Create(test);

        // まとめて書き込むので元に戻せない。断られたらファイル選択まで進まない
        files.Confirms = false;
        vm.ImportLegacyBackupCommand.Execute(null);

        Assert.False(files.WasAskedForFile);
        Assert.Null(vm.StatusMessage);
    }

    /// <summary>リポジトリの中だけを探す。上へ遡りすぎると読めない場所に入る。</summary>
    private static string Find(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.sln").Length == 0)
        {
            directory = directory.Parent;
        }

        if (directory is null) throw new DirectoryNotFoundException("リポジトリの根が見つかりません。");

        return directory.GetFiles(name, SearchOption.AllDirectories).FirstOrDefault()?.FullName
               ?? throw new FileNotFoundException($"{name} が見つかりません。");
    }
}
