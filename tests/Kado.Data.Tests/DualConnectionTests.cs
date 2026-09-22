using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// UI 用と Google 同期用、2本の接続を同じファイルに対して開く構成（<c>App.xaml.cs</c>）の検証。
/// <para>
/// WAL + <c>busy_timeout</c> の設定（<see cref="CalendarDatabase.Connect"/>）が、
/// 2本の接続が同時に読み書きしても例外を出さないことと、片方がトランザクションを
/// 張っている最中でももう片方が（待たされたうえで）書けることを確かめる。
/// ファイルを使う必要があるので一時ディレクトリに作る（メモリ上の DB は WAL にならない）。
/// </para>
/// </summary>
public class DualConnectionTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("slideina-dual-").FullName;

    private static CalendarEvent Sample(string id) => new()
    {
        Id = id,
        Title = $"予定{id}",
        Date = new DateOnly(2026, 9, 24),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>App.xaml.cs と同じく、両方の接続を <c>ConnectAndMigrate</c> で開く。</summary>
    private CalendarDatabase OpenDb() => CalendarDatabase.OpenFile(Path.Combine(_work, "data.db"));

    [Fact]
    public void 二本の接続でマイグレーションを二重に呼んでも安全()
    {
        var db = OpenDb();

        using var a = db.ConnectAndMigrate();
        using var b = db.ConnectAndMigrate();

        // 2本目は既に最新なので何もしない。実際に書き込める状態になっていることも確かめる
        new EventRepository(a).Upsert(Sample("e-a"));
        new EventRepository(b).Upsert(Sample("e-b"));

        Assert.NotNull(new EventRepository(a).Find("e-b"));
        Assert.NotNull(new EventRepository(b).Find("e-a"));
    }

    [Fact]
    public async Task 並行して読み書きしても例外にならない()
    {
        var db = OpenDb();
        using var a = db.ConnectAndMigrate();
        using var b = db.ConnectAndMigrate();

        var repoA = new EventRepository(a);
        var repoB = new EventRepository(b);

        // それぞれの接続から交互に書き込み、もう片方の接続から読む。busy_timeout が
        // 効いていれば SQLITE_BUSY で落ちずに完走する
        var taskA = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                repoA.Upsert(Sample($"a-{i}"));
                _ = repoA.All();
            }
        });

        var taskB = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                repoB.Upsert(Sample($"b-{i}"));
                _ = repoB.All();
            }
        });

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(taskA, taskB));

        Assert.Null(exception);
        Assert.Equal(100, repoA.All().Count);
    }

    [Fact]
    public async Task 片方のトランザクション中でももう片方が書ける()
    {
        var db = OpenDb();
        using var a = db.ConnectAndMigrate();
        using var b = db.ConnectAndMigrate();

        using var transaction = a.BeginTransaction();
        new EventRepository(a).Upsert(Sample("held"), transaction);

        // 別スレッドで b から書く。busy_timeout の間は待たされるはずなので、
        // 先に走らせてからコミットする
        var writeTask = Task.Run(() => new EventRepository(b).Upsert(Sample("from-b")));

        // a がトランザクションを持ったままでも b の書き込みは終わっていないことを確かめてから外す
        await Task.Delay(200);
        Assert.False(writeTask.IsCompleted);

        transaction.Commit();

        // busy_timeout（5秒）の範囲内で b の書き込みが完了する
        var completed = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(5))) == writeTask;
        Assert.True(completed, "busy_timeout の範囲内で書き込みが完了しなかった");

        Assert.NotNull(new EventRepository(a).Find("from-b"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // ハンドルが残っていて消せなくても、テスト結果には関わらない
        }
    }
}
