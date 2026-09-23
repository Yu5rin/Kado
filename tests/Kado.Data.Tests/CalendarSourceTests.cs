using Kado.Data.Models;

namespace Kado.Data.Tests;

/// <summary>
/// <see cref="CalendarSource.IsReadOnly"/>。
/// <para>
/// 送れるかどうか（<c>GoogleSyncService</c>）と、編集・削除・ドラッグを止めるかどうか
/// （<c>MainViewModel</c>）の両方がここを見る。判定を2箇所に分けて持つと、
/// どちらかだけ直し忘れて食い違う。
/// </para>
/// </summary>
public class CalendarSourceTests
{
    private static CalendarSource Calendar(string? googleRaw) => new()
    {
        Id = "c1", Summary = "仕事", GoogleRaw = googleRaw, UpdatedAt = DateTimeOffset.Now,
    };

    [Theory]
    [InlineData("owner")]
    [InlineData("writer")]
    public void owner_と_writer_は書ける(string role)
    {
        var calendar = Calendar($$"""{"id":"c1","accessRole":"{{role}}"}""");

        Assert.False(calendar.IsReadOnly);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("freeBusyReader")]
    public void reader_と_freeBusyReader_は読むだけ(string role)
    {
        var calendar = Calendar($$"""{"id":"c1","accessRole":"{{role}}"}""");

        Assert.True(calendar.IsReadOnly);
    }

    [Fact]
    public void 取り込む前は書けると見なす()
    {
        // 一覧を取り込む前は判断する材料が無い。無いのに読み取り専用扱いにすると、
        // 何も操作できなくなる
        Assert.False(Calendar(null).IsReadOnly);
        Assert.False(Calendar(string.Empty).IsReadOnly);
    }

    [Fact]
    public void accessRoleが無ければ書けると見なす()
    {
        Assert.False(Calendar("""{"id":"c1"}""").IsReadOnly);
    }

    [Fact]
    public void 控えが壊れていても書けると見なす()
    {
        // 読めないなら書けると見なす。書けないものへ送れば断られるだけで、
        // 書けるものを読み取り専用にしてしまうより害が小さい
        Assert.False(Calendar("{壊れたJSON").IsReadOnly);
    }
}
