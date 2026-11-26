using Kado.Data.Backup;

namespace Kado.Data.Tests;

/// <summary>
/// 世代バックアップ。起動時の分は1日1本、取り込み・復元の直前の分は別枠で数える。
/// </summary>
public sealed class AutoBackupServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kado-autobackup-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string[] Names(string pattern) =>
        Directory.GetFiles(_dir, pattern).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void 起動時の分は同じ日に二度取らない()
    {
        using var db = TestDatabase.Create();
        var morning = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        Assert.True(AutoBackupService.TryRunStartup(db.Connection, _dir, morning));
        Assert.True(AutoBackupService.TryRunStartup(db.Connection, _dir, morning.AddHours(5)));

        Assert.Single(Names("data-*.db"));
    }

    [Fact]
    public void 日が変われば起動時の分をまた取る()
    {
        using var db = TestDatabase.Create();
        var day = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        AutoBackupService.TryRunStartup(db.Connection, _dir, day);
        AutoBackupService.TryRunStartup(db.Connection, _dir, day.AddDays(1));

        Assert.Equal(2, Names("data-*.db").Length);
    }

    [Fact]
    public void 起動時の分は直近の世代数だけ残す()
    {
        using var db = TestDatabase.Create();
        var day = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < AutoBackupService.KeepCount + 3; i++)
        {
            AutoBackupService.TryRunStartup(db.Connection, _dir, day.AddDays(i));
        }

        var kept = Names("data-*.db");
        Assert.Equal(AutoBackupService.KeepCount, kept.Length);
        Assert.Contains("data-20260908-080000.db", kept);
        Assert.DoesNotContain("data-20260901-080000.db", kept);
    }

    [Fact]
    public void 取り込みや復元の直前の分は起動時の分を押し出さない()
    {
        using var db = TestDatabase.Create();
        var day = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < AutoBackupService.KeepCount; i++)
        {
            AutoBackupService.TryRunStartup(db.Connection, _dir, day.AddDays(i));
        }

        // 同じ日に何度も操作しても、起動時の分は減らない
        for (var i = 0; i < AutoBackupService.KeepCount + 4; i++)
        {
            Assert.True(AutoBackupService.TryRunSafety(db.Connection, _dir, day.AddDays(6).AddMinutes(i)));
        }

        Assert.Equal(AutoBackupService.KeepCount, Names("data-*.db").Length);
        Assert.Contains("data-20260901-080000.db", Names("data-*.db"));
        Assert.Equal(AutoBackupService.KeepCount, Names("safety-*.db").Length);
    }

    [Fact]
    public void 操作の直前の分は同じ日でも毎回取る()
    {
        using var db = TestDatabase.Create();
        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        AutoBackupService.TryRunSafety(db.Connection, _dir, at);
        AutoBackupService.TryRunSafety(db.Connection, _dir, at.AddMinutes(1));

        Assert.Equal(2, Names("safety-*.db").Length);
    }
}
