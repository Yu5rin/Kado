using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>
/// バックアップの保存と復元。
/// <para>
/// データベースそのものを扱うのは App 側なので、ここで確かめるのは
/// 呼び出しの筋道（選ばせる・尋ねる・渡す）まで。
/// </para>
/// </summary>
public class BackupCommandTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Fact]
    public void 保存先を選ぶと書き出しに渡る()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = @"C:\temp\backup.db" };
        string? saved = null;

        var main = new MainViewModel(test.Workspace, Today, files: files) { SaveBackup = p => saved = p };
        main.BackupCommand.Execute(null);

        Assert.Equal(@"C:\temp\backup.db", saved);
        Assert.Contains("バックアップ", main.StatusMessage);
    }

    [Fact]
    public void 既定の名前に日時が入る()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = @"C:\temp\backup.db" };

        var main = new MainViewModel(test.Workspace, Today, files: files) { SaveBackup = _ => { } };
        main.BackupCommand.Execute(null);

        Assert.StartsWith("Kado-", files.LastSuggestedName);
        Assert.EndsWith(".db", files.LastSuggestedName);
    }

    [Fact]
    public void 取り消したら何もしない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = null };
        var called = false;

        var main = new MainViewModel(test.Workspace, Today, files: files) { SaveBackup = _ => called = true };
        main.BackupCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void 復元は尋ねてから渡す()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToPick = @"C:\temp\backup.db", Confirms = true };
        string? restored = null;

        var main = new MainViewModel(test.Workspace, Today, files: files) { RestoreBackup = p => restored = p };
        main.RestoreCommand.Execute(null);

        Assert.Equal(@"C:\temp\backup.db", restored);
    }

    [Fact]
    public void 復元は断られたら何もしない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToPick = @"C:\temp\backup.db", Confirms = false };
        var called = false;

        var main = new MainViewModel(test.Workspace, Today, files: files) { RestoreBackup = _ => called = true };
        main.RestoreCommand.Execute(null);

        // いまの内容がすべて置き換わる。尋ねずに進めてはいけない
        Assert.False(called);
    }

    [Fact]
    public void 復元の口が無ければ押せない()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);

        Assert.False(main.RestoreCommand.CanExecute(null));
    }

    /// <summary>復元の間だけ IsBusy が立つ（項目5）。終わったら必ず戻る。</summary>
    [Fact]
    public void 復元の間だけビジーになる()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToPick = @"C:\temp\backup.db", Confirms = true };

        var main = new MainViewModel(test.Workspace, Today, files: files) { RestoreBackup = _ => { } };

        var wasBusy = false;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy) && main.IsBusy) wasBusy = true;
        };

        main.RestoreCommand.Execute(null);

        Assert.True(wasBusy);
        Assert.False(main.IsBusy);
    }
}
