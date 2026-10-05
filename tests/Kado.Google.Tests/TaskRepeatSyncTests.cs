using Microsoft.Data.Sqlite;
using Kado.Data;
using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Sync;

namespace Kado.Google.Tests;

/// <summary>
/// Google 側（Google のアプリ・Web）で、Kado の繰り返し付きタスクが完了にされたとき。
/// <para>
/// 繰り返しは Kado だけの項目で、Google は知らない。受け取って「手元では未完了で繰り返しあり」だった
/// タスクが「完了」になったと分かったら、Kado の中で完了にしたときと同じに、次の回を手元に作る。
/// 次の回は新規タスクなので、受け取りのあとの送信で（同じ同期のうちに）Google に作られる。
/// </para>
/// <para>時計は <see cref="ManualClock"/>（2026/9/19 土曜）。</para>
/// </summary>
public class TaskRepeatSyncTests : IDisposable
{
    private const string WeeklyMonday = "FREQ=WEEKLY;BYDAY=MO";

    private readonly SqliteConnection _connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTaskGateway _remote;

    public TaskRepeatSyncTests() => _remote = new FakeTaskGateway(_clock);

    private TaskRepository Tasks => new(_connection);

    private TaskSyncEngine EngineWith(Func<RepeatEnvironment>? environment = null) =>
        new(Tasks, new TombstoneRepository(_connection), new SettingsRepository(_connection), _remote, _clock, environment);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    public void Dispose() => _connection.Dispose();

    /// <summary>Google 側で完了にしたことにする。</summary>
    private void CompleteOnGoogle(string id)
    {
        _remote.Items[id]["status"] = "completed";
        _remote.Items[id]["completed"] = "2026-09-19T03:00:00.000Z";

        // 書き換わった印（updatedMin に拾われる）
        _remote.Edit(id, _remote.Items[id]["title"]!.GetValue<string>());
    }

    /// <summary>期限 2026/9/21(月)の「週報」を Google に置いて取り込み、手元で繰り返しを付ける。</summary>
    private async Task SeedRepeatingAsync(string repeat = WeeklyMonday, string due = "2026-09-21")
    {
        _remote.Add("g1", "週報", due: due);

        // メモは Google が持つ項目。Google 側に置いておく
        _remote.Items["g1"]["notes"] = "メモ";
        await EngineWith().SyncAsync("@default", "local:mytasks");

        // 繰り返しと URL は Kado だけの項目
        Tasks.Upsert(Tasks.All().Single() with { Repeat = repeat, Url = "https://example.com/spec" });
    }

    [Fact]
    public async Task Google側で完了にされたら次の回を作り完了したほうから繰り返しを外す()
    {
        await SeedRepeatingAsync();

        CompleteOnGoogle("g1");
        var report = await EngineWith().SyncAsync("@default", "local:mytasks");

        var all = Tasks.All();
        Assert.Equal(2, all.Count);

        var completed = all.Single(t => t.GoogleTaskId == "g1");
        Assert.True(completed.IsDone);
        Assert.Null(completed.Repeat);

        // 次の回：期限は 9/21 の次の月曜 9/28。中身と繰り返しを引き継ぐ
        var next = all.Single(t => t.Id != completed.Id);
        Assert.False(next.IsDone);
        Assert.Equal(D(2026, 9, 28), next.Due);
        Assert.Equal("週報", next.Title);
        Assert.Equal("メモ", next.Note);
        Assert.Equal("https://example.com/spec", next.Url);
        Assert.Equal(WeeklyMonday, next.Repeat);
        Assert.Equal("local:mytasks", next.TaskListId);

        // 作ったことを残す
        Assert.Contains(report.Warnings, w => w == "繰り返しの次の回を作りました：週報 9/28(月)");
    }

    [Fact]
    public async Task 次の回は同じ同期のうちに新規タスクとしてGoogleに送られる()
    {
        await SeedRepeatingAsync();

        CompleteOnGoogle("g1");
        var report = await EngineWith().SyncAsync("@default", "local:mytasks");

        // 受け取りのあとの送信が、まだ Google の ID を持たないタスクを新規として送る
        Assert.True(report.CreatedLocal >= 1);
        Assert.Equal(1, report.CreatedRemote);

        var sent = _remote.Items.Values.Single(i => i["id"]!.GetValue<string>() != "g1");
        Assert.Equal("週報", sent["title"]!.GetValue<string>());
        Assert.StartsWith("2026-09-28", sent["due"]!.GetValue<string>());
        Assert.Equal("needsAction", sent["status"]!.GetValue<string>());

        // Kado だけの項目は送られない
        foreach (var key in new[] { "repeat", "Repeat", "recurrence", "url", "attachments" })
        {
            Assert.False(sent.ContainsKey(key), $"Google に「{key}」が送られている");
        }

        // 手元の次の回は Google の ID と結び付いた
        var next = Tasks.All().Single(t => t.GoogleTaskId != "g1");
        Assert.NotNull(next.GoogleTaskId);
        Assert.Equal(WeeklyMonday, next.Repeat);
    }

    [Fact]
    public async Task 同じ完了を2度受け取っても次の回は二重に作らない()
    {
        await SeedRepeatingAsync();

        CompleteOnGoogle("g1");
        await EngineWith().SyncAsync("@default", "local:mytasks");
        Assert.Equal(2, Tasks.All().Count);

        // Google 側でもう一度更新が来た（同じ完了を、また受け取る）
        CompleteOnGoogle("g1");
        var second = await EngineWith().SyncAsync("@default", "local:mytasks");

        Assert.Equal(2, Tasks.All().Count);
        Assert.DoesNotContain(second.Warnings, w => w.Contains("繰り返しの次の回を作りました"));
    }

    [Fact]
    public async Task 繰り返しの無いタスクが完了にされても何も作らない()
    {
        await SeedRepeatingAsync();
        Tasks.Upsert(Tasks.All().Single() with { Repeat = null });

        CompleteOnGoogle("g1");
        var report = await EngineWith().SyncAsync("@default", "local:mytasks");

        Assert.Single(Tasks.All());
        Assert.DoesNotContain(report.Warnings, w => w.Contains("繰り返しの次の回を作りました"));
    }

    [Fact]
    public async Task 手元ですでに完了にしていたタスクは作らない()
    {
        await SeedRepeatingAsync();

        // こちらで完了にした（Kado の中なら次の回を作って繰り返しを外す）。ここでは外した姿を直接置く
        Tasks.Upsert(Tasks.All().Single() with
        {
            IsDone = true, CompletedAt = new DateTimeOffset(2026, 9, 19, 3, 0, 0, TimeSpan.Zero), Repeat = null,
        });

        CompleteOnGoogle("g1");
        await EngineWith().SyncAsync("@default", "local:mytasks");

        Assert.Single(Tasks.All());
    }

    [Fact]
    public async Task 読めない繰り返しは次の回を作らず文字列も消さない()
    {
        await SeedRepeatingAsync(repeat: "こわれた文字列");

        CompleteOnGoogle("g1");
        var report = await EngineWith().SyncAsync("@default", "local:mytasks");

        var only = Assert.Single(Tasks.All());
        Assert.True(only.IsDone);
        Assert.Equal("こわれた文字列", only.Repeat);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("繰り返しの次の回を作りました"));
    }

    [Fact]
    public async Task 期限の無いタスクは次の回を作らない()
    {
        await SeedRepeatingAsync();
        Tasks.Upsert(Tasks.All().Single() with { Due = null });
        _remote.Items["g1"]["due"] = null;

        CompleteOnGoogle("g1");
        await EngineWith().SyncAsync("@default", "local:mytasks");

        Assert.Single(Tasks.All());
    }

    [Fact]
    public async Task 稼働日基準の次の回は渡した稼働日と週の始まりで決まる()
    {
        // 月末の最後の稼働日。9/30(水)が休みなら 9/29(火)
        await SeedRepeatingAsync(repeat: "X-KADO=MONTH-LAST-WORKDAY", due: "2026-08-31");

        var calls = 0;
        RepeatEnvironment Environment()
        {
            calls++;
            return new RepeatEnvironment(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                                                 && date != D(2026, 9, 30), DayOfWeek.Monday);
        }

        CompleteOnGoogle("g1");
        await EngineWith(Environment).SyncAsync("@default", "local:mytasks");

        var next = Tasks.All().Single(t => !t.IsDone);
        Assert.Equal(D(2026, 9, 29), next.Due);
        Assert.Equal("X-KADO=MONTH-LAST-WORKDAY", next.Repeat);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 稼働日や設定は次の回が要るときにだけ読む()
    {
        await SeedRepeatingAsync();

        var calls = 0;
        RepeatEnvironment Environment()
        {
            calls++;
            return RepeatEnvironment.Default;
        }

        // 何も完了にされていない同期では読まない（重い組み立てを毎回しない）
        _remote.Edit("g1", "週報（Googleで変更）");
        await EngineWith(Environment).SyncAsync("@default", "local:mytasks");

        Assert.Equal(0, calls);
    }
}
