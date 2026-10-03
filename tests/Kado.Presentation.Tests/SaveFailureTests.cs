using Dapper;
using Kado.Data.Repositories;
using Kado.Presentation.Editing;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;
using Microsoft.Data.Sqlite;

namespace Kado.Presentation.Tests;

/// <summary>
/// 書き込みの失敗（ディスクがいっぱい・5秒待っても取れないロック）で、アプリを落とさない。
/// <para>
/// 失敗は、表に仕掛けたトリガー（<c>RAISE(ABORT)</c>）で作る。SQLite が書き込みを断ったときと
/// 同じく <see cref="SqliteException"/> になる。
/// </para>
/// </summary>
public class SaveFailureTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static void FailWrites(TestWorkspace test, string table, string kind = "INSERT")
    {
        test.Connection.Execute(
            $"CREATE TRIGGER fail_{table}_{kind} BEFORE {kind} ON {table} BEGIN SELECT RAISE(ABORT, 'disk full'); END;");
    }

    private static (MainViewModel Main, FakeFileDialogs Files) CreateMain(TestWorkspace test)
    {
        var files = new FakeFileDialogs();
        return (new MainViewModel(test.Workspace, Today, files: files), files);
    }

    // ------------------------------------------------------------------
    // 編集の入口（項目8）
    // ------------------------------------------------------------------

    [Fact]
    public void 一行入力で書けなくても落ちず保存できなかったと伝える()
    {
        using var test = TestWorkspace.Create();
        var (main, files) = CreateMain(test);
        FailWrites(test, "events");

        main.QuickText = "明日 打合せ";
        main.QuickCommand.Execute(null);

        Assert.Contains("保存できませんでした", main.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("ディスクの空き", files.LastReport, StringComparison.Ordinal);

        // 入れ損ねた内容は残す。書き直さなくて済む
        Assert.Equal("明日 打合せ", main.QuickText);

        // 失敗した編集は履歴に積まない（積むと、存在しない予定を「元に戻す」ことになる）
        Assert.False(main.CanUndo);
    }

    [Fact]
    public void タスクを書けなくても落ちない()
    {
        using var test = TestWorkspace.Create();
        var (main, _) = CreateMain(test);
        FailWrites(test, "tasks");

        main.QuickText = "□ 資料を作る";
        main.QuickCommand.Execute(null);

        Assert.Contains("保存できませんでした", main.StatusMessage, StringComparison.Ordinal);
        Assert.False(main.CanUndo);
    }

    [Fact]
    public void 元に戻すが書けなくても落ちず履歴も失わない()
    {
        using var test = TestWorkspace.Create();
        var (main, _) = CreateMain(test);

        main.QuickText = "明日 打合せ";
        main.QuickCommand.Execute(null);
        Assert.True(main.CanUndo);

        // 元に戻すのは削除。これだけが失敗する
        FailWrites(test, "events", "DELETE");
        main.UndoCommand.Execute(null);

        Assert.Contains("保存できませんでした", main.StatusMessage, StringComparison.Ordinal);

        // 戻せなかったので、「戻せるもの」のまま残る。やり直しへ移っていない
        Assert.True(main.CanUndo);
        Assert.False(main.CanRedo);
    }

    [Fact]
    public void 元に戻す履歴は戻す処理に失敗したら動かない()
    {
        var edit = new FlakyEdit();
        var stack = new UndoStack();

        stack.Execute(edit);

        edit.FailNext = true;
        Assert.Throws<SqliteException>(() => stack.Undo());
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);

        // 直ったあとにもう一度押せば、戻せる
        Assert.Equal("テスト", stack.Undo());
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);

        edit.FailNext = true;
        Assert.Throws<SqliteException>(() => stack.Redo());
        Assert.True(stack.CanRedo);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void 実行に失敗した編集は履歴に積まれない()
    {
        var edit = new FlakyEdit { FailNext = true };
        var stack = new UndoStack();

        Assert.Throws<SqliteException>(() => stack.Execute(edit));

        Assert.False(stack.CanUndo);
    }

    private sealed class FlakyEdit : IUndoableEdit
    {
        public bool FailNext { get; set; }

        public string Description => "テスト";

        public void Apply() => MaybeFail();

        public void Revert() => MaybeFail();

        private void MaybeFail()
        {
            if (!FailNext) return;

            FailNext = false;
            throw new SqliteException("database or disk is full", 13);
        }
    }

    // ------------------------------------------------------------------
    // 設定の保存（項目8）
    // ------------------------------------------------------------------

    [Fact]
    public void 設定を書けなくても落ちず値はこの実行のあいだ効く()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(new SettingsRepository(test.Connection));
        FailWrites(test, "settings");

        var failures = new List<SqliteException>();
        var changed = 0;
        settings.SaveFailed += (_, ex) => failures.Add(ex);
        settings.Changed += (_, _) => changed++;

        settings.Theme = ThemeChoice.Dark;
        settings.CheckForUpdateOnStartup = false;

        Assert.Equal(2, failures.Count);

        // メモリ上は変わっていて、画面にも知らされる
        Assert.Equal(ThemeChoice.Dark, settings.Theme);
        Assert.False(settings.CheckForUpdateOnStartup);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void 設定を書けなかったことは画面側が一度だけ断る()
    {
        using var test = TestWorkspace.Create();
        var settings = new AppSettings(new SettingsRepository(test.Connection));
        var files = new FakeFileDialogs();
        var main = new MainViewModel(test.Workspace, Today, settings: settings, files: files);
        FailWrites(test, "settings");

        settings.Theme = ThemeChoice.Dark;
        Assert.Contains("保存できませんでした", main.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("設定を保存できませんでした", files.LastReportTitle);

        // 設定は連続して変わる。2度目以降は、断りの窓で止めない
        files.ResetReport();
        settings.WeekStart = DayOfWeek.Monday;
        Assert.Null(files.LastReport);
    }

    [Fact]
    public void ペインの幅を書けなくても落ちない()
    {
        using var test = TestWorkspace.Create();
        var (main, files) = CreateMain(test);
        FailWrites(test, "settings");

        main.SidePanelWidth = main.SidePanelWidth + 40;

        // 見た目の記憶なので、断りも出さない
        Assert.Null(files.LastReport);
    }

    [Fact]
    public void 窓とドックの置き場所を書けなくても落ちない()
    {
        using var test = TestWorkspace.Create();
        var repository = new SettingsRepository(test.Connection);
        FailWrites(test, "settings");

        new WindowPlacementStore(repository).Save(new WindowPlacement(10, 20, 900, 700, false));
        new DockPlacementStore(repository).Save(
            new DockPlacement(ShellMode.Overlay, DockEdge.Right, 352, null));
        new DockPlacementStore(repository).SetWorkAreaReserved(true);
    }

    // ------------------------------------------------------------------
    // バックアップ・取り込み・書き出し（項目4）
    // ------------------------------------------------------------------

    [Fact]
    public void 書き込めない保存先へのバックアップで落ちない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = "/読み取り専用/backup.db" };
        var main = new MainViewModel(test.Workspace, Today, files: files)
        {
            SaveBackup = _ => throw new SqliteException("attempt to write a readonly database", 8),
        };

        main.BackupCommand.Execute(null);

        Assert.Contains("バックアップを書き出せませんでした", main.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("書き込めない", main.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteException", main.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ディスクがいっぱいでバックアップに失敗しても落ちない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToSave = "/満杯/backup.db" };
        var main = new MainViewModel(test.Workspace, Today, files: files)
        {
            SaveBackup = _ => throw new SqliteException("database or disk is full", 13),
        };

        main.BackupCommand.Execute(null);

        Assert.Contains("ディスクの空き", main.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 復元で読めないバックアップを選んでも落ちない()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToPick = "/選んだ/not-a-db.db", Confirms = true };
        var main = new MainViewModel(test.Workspace, Today, files: files)
        {
            RestoreBackup = _ => throw new SqliteException("file is not a database", 26),
        };
        main.RestoreCommand.RaiseCanExecuteChanged();

        main.RestoreCommand.Execute(null);

        Assert.Contains("復元できませんでした", main.StatusMessage, StringComparison.Ordinal);
        Assert.False(main.IsBusy);
    }

    [Fact]
    public void 取り込みの書き込みが失敗しても落ちず理由を出す()
    {
        using var test = TestWorkspace.Create();
        var files = new FakeFileDialogs { FileToPick = FindLegacyFile(), Confirms = true };
        var main = new MainViewModel(test.Workspace, Today, files: files);
        FailWrites(test, "events");

        main.ImportLegacyBackupCommand.Execute(null);

        Assert.Contains("失敗しました", main.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("取り込めませんでした", files.LastReport, StringComparison.Ordinal);
        Assert.False(main.IsBusy);
    }

    private static string FindLegacyFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.sln").Length == 0) directory = directory.Parent;

        return directory!.GetFiles("inaCalendar-backup-sample.json", SearchOption.AllDirectories).First().FullName;
    }

    // ------------------------------------------------------------------
    // 画面のスレッドで漏れてきたときの最後の網
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(5)]   // BUSY
    [InlineData(6)]   // LOCKED
    [InlineData(13)]  // FULL
    [InlineData(8)]   // READONLY
    [InlineData(10)]  // IOERR
    public void 直せる種類の書き込み失敗は案内して続ける(int code)
    {
        var failure = new SqliteException("x", code);

        Assert.True(UnhandledFailurePolicy.IsRecoverable(failure));
        Assert.Contains("保存できませんでした", UnhandledFailurePolicy.Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void 包まれていても見分ける()
    {
        var wrapped = new System.Reflection.TargetInvocationException(new SqliteException("x", 13));

        Assert.True(UnhandledFailurePolicy.IsRecoverable(wrapped));
        Assert.True(UnhandledFailurePolicy.IsRecoverable(new AggregateException(new SqliteException("x", 5))));
    }

    [Theory]
    [InlineData(11)]  // CORRUPT
    [InlineData(1)]   // ERROR（想定外）
    public void 壊れているものと想定外は続けない(int code)
    {
        Assert.False(UnhandledFailurePolicy.IsRecoverable(new SqliteException("x", code)));
    }

    [Fact]
    public void データベース以外の例外は続けない()
    {
        Assert.False(UnhandledFailurePolicy.IsRecoverable(new InvalidOperationException()));
        Assert.False(UnhandledFailurePolicy.IsRecoverable(new NullReferenceException()));
    }

    [Fact]
    public void 同じ失敗の案内は続けて出さない()
    {
        var now = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

        Assert.True(UnhandledFailurePolicy.ShouldNotify(null, now));
        Assert.False(UnhandledFailurePolicy.ShouldNotify(now, now.AddSeconds(30)));
        Assert.True(UnhandledFailurePolicy.ShouldNotify(now, now.AddMinutes(1)));
    }
}
