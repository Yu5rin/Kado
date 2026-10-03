using Kado.Data.Repositories;

namespace Kado.Data.Tests;

/// <summary>消した記録を1件引く。削除を元に戻すとき、まだ伝えていないかを見分けるのに使う。</summary>
public class TombstoneFindTests
{
    [Fact]
    public void 記録を引ける()
    {
        using var db = TestDatabase.Create();
        var repository = new TombstoneRepository(db.Connection);

        repository.Record("e1", TombstoneRepository.EventKind, "g1", DateTimeOffset.Now, "cal-a");

        var found = repository.Find("e1", TombstoneRepository.EventKind);

        Assert.NotNull(found);
        Assert.Equal("g1", found.GoogleId);
        Assert.Equal("cal-a", found.SourceId);
    }

    [Fact]
    public void 無ければnull()
    {
        using var db = TestDatabase.Create();
        var repository = new TombstoneRepository(db.Connection);

        Assert.Null(repository.Find("e1", TombstoneRepository.EventKind));
    }

    [Fact]
    public void 種類が違えば別のもの()
    {
        using var db = TestDatabase.Create();
        var repository = new TombstoneRepository(db.Connection);

        repository.Record("x1", TombstoneRepository.EventKind, "g1", DateTimeOffset.Now, "cal-a");

        Assert.Null(repository.Find("x1", TombstoneRepository.TaskKind));
    }
}
