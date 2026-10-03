using Kado.Core;
using Kado.Data.Models;
using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>
/// 端末のタイムゾーンの差し替え（<see cref="LocalZone"/>）を使うテストの置き場。ほかのテストと並べて走らせない。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTimeZoneCollection
{
    public const string Name = "端末のタイムゾーンを書き換える";
}

/// <summary>
/// 端末のタイムゾーンが変わったあとも、完了日の判定が古いゾーンのまま動かないこと。
/// <para>
/// トレイに常駐して何日も動くので、出張や夏時間・設定の変更でゾーンが変わることがある。
/// 作った時点の <see cref="TimeZoneInfo.Local"/> を握っていると、アプリを開き直すまで古い
/// ゾーンで完了日を出し続けていた。
/// </para>
/// </summary>
[Collection(LocalTimeZoneCollection.Name)]
public class LocalTimeZoneChangeTests : IDisposable
{
    // 東京（UTC+9）とロサンゼルスの夏時間（UTC-7）。環境変数 TZ は Windows の .NET が読まないので、
    // OS に頼らず固定のゾーンを作って、端末のタイムゾーンの入口（LocalZone）を差し替える
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.CreateCustomTimeZone("Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");
    private static readonly TimeZoneInfo LosAngeles =
        TimeZoneInfo.CreateCustomTimeZone("LosAngeles", TimeSpan.FromHours(-7), "LosAngeles", "LosAngeles");

    private IDisposable? _zone;

    private void UseZone(TimeZoneInfo zone)
    {
        // 前の差し替えを先に戻してから差し替える。Dispose の戻し先が、いつも元の状態になる
        _zone?.Dispose();
        _zone = LocalZone.OverrideForTest(zone);
    }

    public void Dispose() => _zone?.Dispose();

    private static TaskItem DoneAt(DateTimeOffset at) => new()
    {
        Id = "t1", Title = "済み", IsDone = true, CompletedAt = at,
    };

    [Fact]
    public void ゾーンを変えたあとの完了日は新しいゾーンで出す()
    {
        UseZone(Tokyo);
        using var db = TestDatabase.Create();

        // ゾーンを変える前に作る（いまのアプリは起動時に1度だけ作る）
        var query = new ScheduleQuery(new EventRepository(db.Connection), new TaskRepository(db.Connection));

        // 2026-09-24 20:00 UTC は、東京では 9/25 5:00、ロサンゼルスでは 9/24 13:00
        var task = DoneAt(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 9, 25), query.CompletedDate(task));

        UseZone(LosAngeles);

        Assert.Equal(new DateOnly(2026, 9, 24), query.CompletedDate(task));
    }

    [Fact]
    public void ゾーンを渡したときはそのゾーンに固定する()
    {
        UseZone(Tokyo);
        using var db = TestDatabase.Create();
        var query = new ScheduleQuery(
            new EventRepository(db.Connection), new TaskRepository(db.Connection), TimeZoneInfo.Utc);

        UseZone(LosAngeles);

        // 渡したゾーン（UTC）のまま。端末のゾーンには引きずられない
        var task = DoneAt(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 9, 24), query.CompletedDate(task));
    }
}
