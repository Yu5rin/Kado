using SlideinaCalendar.Core.Input;

namespace SlideinaCalendar.Core.Tests;

/// <summary>
/// 1行から予定を読み取る。旧 inaCalendar と同じ読み方になっているか。
/// </summary>
public class QuickParserTests
{
    /// <summary>2026年9月24日（木）。</summary>
    private static readonly DateOnly Base = new(2026, 9, 24);

    private static QuickEntry Parse(string text) => QuickParser.Parse(text, Base);

    // ------------------------------------------------------------------
    // explicitBase（選んでいる日）。今日と選んでいる日を分けて渡せるか
    // ------------------------------------------------------------------

    /// <summary>選んでいる日。Base の6日後。</summary>
    private static readonly DateOnly Selected = new(2026, 9, 30);

    [Fact]
    public void explicitBaseを渡さなければ今までどおり基準日を使う()
    {
        var entry = QuickParser.Parse("打合せ 15時", Base);

        Assert.Equal(Base, entry.Date);
    }

    [Fact]
    public void 日付を書かなければexplicitBaseに入る()
    {
        var entry = QuickParser.Parse("打合せ 15時", Base, Selected);

        Assert.Equal(Selected, entry.Date);
    }

    [Fact]
    public void 相対語はexplicitBaseを渡してもBase基準のまま()
    {
        Assert.Equal(Base.AddDays(1), QuickParser.Parse("明日 打合せ", Base, Selected).Date);
        Assert.Equal(Base.AddDays(3), QuickParser.Parse("3日後 確認", Base, Selected).Date);
        Assert.Equal(new DateOnly(2026, 10, 15), QuickParser.Parse("来月15日 締切", Base, Selected).Date);
    }

    [Fact]
    public void 月を省いた日にちはexplicitBaseの月で読む()
    {
        // Selected は 9/30。月を省いた「5日」は選んでいる日の月＝9月として読む
        Assert.Equal(new DateOnly(2026, 9, 5), QuickParser.Parse("5日 確認", Base, Selected).Date);
    }

    [Fact]
    public void 月日を書いた絶対日付はexplicitBaseを見ない()
    {
        // 「12/25」は日付そのものを書いているので、選んでいる日は関係ない
        Assert.Equal(new DateOnly(2026, 12, 25), QuickParser.Parse("12/25 棚卸", Base, Selected).Date);
    }

    [Fact]
    public void 明日15時打合せ()
    {
        var entry = Parse("明日15時 打合せ @会議室A");

        Assert.Equal(new DateOnly(2026, 9, 25), entry.Date);
        Assert.Equal(new TimeOnly(15, 0), entry.Start);
        Assert.Equal("会議室A", entry.Location);
        Assert.Equal("打合せ", entry.Title);
        Assert.True(entry.CanCommit);
    }

    [Theory]
    [InlineData("今日", 0)]
    [InlineData("本日", 0)]
    [InlineData("明日", 1)]
    [InlineData("あした", 1)]
    [InlineData("明後日", 2)]
    [InlineData("あさって", 2)]
    [InlineData("明々後日", 3)]
    [InlineData("しあさって", 3)]
    [InlineData("昨日", -1)]
    public void 相対の言い方(string word, int offset)
    {
        Assert.Equal(Base.AddDays(offset), Parse($"{word} 会議").Date);
    }

    [Fact]
    public void 曜日だけなら次に来るその曜日()
    {
        // 基準は木曜。次の月曜は 9/28
        Assert.Equal(new DateOnly(2026, 9, 28), Parse("月曜 定例").Date);
    }

    [Fact]
    public void 同じ曜日はその日のまま()
    {
        Assert.Equal(Base, Parse("木曜日 定例").Date);
    }

    [Fact]
    public void 来週と再来週()
    {
        // 基準の週は 9/20(日)〜9/26(土)。来週の月曜は 9/28、再来週は 10/5
        Assert.Equal(new DateOnly(2026, 9, 28), Parse("来週月曜 定例").Date);
        Assert.Equal(new DateOnly(2026, 10, 5), Parse("再来週月曜 定例").Date);
    }

    [Fact]
    public void 来週だけなら7日後()
    {
        Assert.Equal(Base.AddDays(7), Parse("来週 打合せ").Date);
    }

    [Fact]
    public void 来月の日にち()
    {
        Assert.Equal(new DateOnly(2026, 10, 15), Parse("来月15日 締切").Date);
    }

    [Theory]
    [InlineData("2026/12/25", 2026, 12, 25)]
    [InlineData("2026-12-25", 2026, 12, 25)]
    [InlineData("2026年12月25日", 2026, 12, 25)]
    [InlineData("12月25日", 2026, 12, 25)]
    [InlineData("12/25", 2026, 12, 25)]
    public void 日付の書き方(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), Parse($"{text} 納品").Date);
    }

    [Fact]
    public void 過ぎた月日は翌年と読む()
    {
        // 基準は 9/24。3/1 はもう過ぎているので 2027 年
        Assert.Equal(new DateOnly(2027, 3, 1), Parse("3/1 棚卸").Date);
    }

    [Fact]
    public void N日後とN日()
    {
        Assert.Equal(Base.AddDays(5), Parse("5日後 確認").Date);
        Assert.Equal(new DateOnly(2026, 9, 5), Parse("5日 確認").Date);
    }

    [Fact]
    public void N日間は期間なので日付に取らない()
    {
        var entry = Parse("3日間 出張");

        Assert.Equal(Base, entry.Date);
        Assert.Contains("3日間", entry.Title);
    }

    [Fact]
    public void 暦に無い日は止める()
    {
        var entry = Parse("2月30日 会議");

        Assert.True(entry.HasDateError);
        Assert.False(entry.CanCommit);
    }

    [Theory]
    [InlineData("15:30", 15, 30)]
    [InlineData("15時半", 15, 30)]
    [InlineData("15時", 15, 0)]
    [InlineData("15時45分", 15, 45)]
    [InlineData("午後3時", 15, 0)]
    [InlineData("午前9時", 9, 0)]
    [InlineData("午前12時", 0, 0)]
    [InlineData("午後12時", 12, 0)]
    public void 時刻の書き方(string text, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), Parse($"{text} 打合せ").Start);
    }

    [Fact]
    public void 時間の範囲()
    {
        var entry = Parse("10:00-11:30 打合せ");

        Assert.Equal(new TimeOnly(10, 0), entry.Start);
        Assert.Equal(new TimeOnly(11, 30), entry.End);
    }

    [Fact]
    public void 漢字の範囲()
    {
        var entry = Parse("10時から11時半 打合せ");

        Assert.Equal(new TimeOnly(10, 0), entry.Start);
        Assert.Equal(new TimeOnly(11, 30), entry.End);
    }

    [Fact]
    public void N時間は所要時間なので時刻に取らない()
    {
        var entry = Parse("2時間 作業");

        Assert.Null(entry.Start);
        Assert.Contains("2時間", entry.Title);
    }

    [Fact]
    public void 無い時刻は止める()
    {
        var entry = Parse("25:00 会議");

        Assert.True(entry.HasTimeError);
        Assert.False(entry.CanCommit);
    }

    [Theory]
    [InlineData("毎週月曜 定例")]
    [InlineData("来年4月 入社式")]
    [InlineData("9/1-9/3 出張")]
    public void 読めない言い回しは登録を止める(string text)
    {
        var entry = Parse(text);

        Assert.NotNull(entry.UnsupportedWord);
        Assert.False(entry.CanCommit);
    }

    [Fact]
    public void 終日は時刻を書かなければどのみち終日なので読み飛ばす()
    {
        var entry = Parse("終日 出張");

        Assert.Null(entry.UnsupportedWord);
        Assert.Null(entry.Start);
        Assert.Equal("出張", entry.Title);
        Assert.True(entry.CanCommit);
    }

    [Fact]
    public void 終日と時刻を両方書いても時刻を優先して読む()
    {
        var entry = Parse("終日 15時 出張");

        Assert.Null(entry.UnsupportedWord);
        Assert.Equal(new TimeOnly(15, 0), entry.Start);
        Assert.Equal("出張", entry.Title);
    }

    // ------------------------------------------------------------------
    // タスクかどうかの判定（項目2）
    // ------------------------------------------------------------------

    [Fact]
    public void 印が無ければ予定として読む()
    {
        var entry = Parse("明日 打合せ");

        Assert.Equal(QuickEntryKind.Event, entry.Kind);
        Assert.False(entry.IsTask);
    }

    [Theory]
    [InlineData("□ 部品表確認")]
    [InlineData("☐ 部品表確認")]
    [InlineData("- 部品表確認")]
    [InlineData("todo 部品表確認")]
    [InlineData("todo: 部品表確認")]
    [InlineData("TODO：部品表確認")]
    [InlineData("タスク 部品表確認")]
    [InlineData("タスク: 部品表確認")]
    [InlineData("タスク：部品表確認")]
    public void 先頭の印でタスクと読む(string text)
    {
        var entry = Parse(text);

        Assert.True(entry.IsTask);
        Assert.Equal("部品表確認", entry.Title);
    }

    [Fact]
    public void 印つきでも日付は今までどおり読める()
    {
        // 「明日」は今までどおり相対語として読める。印を外したぶんだけ渡す
        var entry = Parse("todo 明日 部品表確認");

        Assert.True(entry.IsTask);
        Assert.Equal(Base.AddDays(1), entry.Date);
        Assert.Equal("部品表確認", entry.Title);
    }

    [Fact]
    public void ハイフンは直後に空白が無ければ印として扱わない()
    {
        // 「9/1-9/3」のような日付の区切りと衝突させない
        var entry = Parse("9/1-9/3 出張");

        Assert.False(entry.IsTask);
    }

    [Fact]
    public void 場所は全角のアットでも読む()
    {
        Assert.Equal("第2会議室", Parse("打合せ ＠第2会議室").Location);
    }

    [Fact]
    public void 何も書かれていなければ基準日のまま()
    {
        var entry = Parse("打合せ");

        Assert.Equal(Base, entry.Date);
        Assert.Null(entry.Start);
        Assert.Equal("打合せ", entry.Title);
    }

    [Fact]
    public void 題が無ければ登録しない()
    {
        Assert.False(Parse("明日15時").CanCommit);
        Assert.False(Parse("").CanCommit);
    }

    [Fact]
    public void 全角数字の日にちは半角と同じに読む()
    {
        var zenkaku = Parse("１５日 打合せ");
        var hankaku = Parse("15日 打合せ");

        Assert.Equal(hankaku.Date, zenkaku.Date);
        Assert.Equal(hankaku.Title, zenkaku.Title);
        Assert.True(zenkaku.CanCommit);
    }

    [Theory]
    [InlineData("２０２６/１０/１ 会議")]
    [InlineData("2026／10／1 会議")]
    public void 全角の数字や区切りでも年月日として読める(string text)
    {
        var entry = Parse(text);

        Assert.Equal(new DateOnly(2026, 10, 1), entry.Date);
        Assert.Equal("会議", entry.Title);
        Assert.True(entry.CanCommit);
    }

    [Theory]
    [InlineData("明日 １０:００ 打合せ")]
    [InlineData("明日 10：00 打合せ")]
    public void 全角の数字やコロンでも時刻として読める(string text)
    {
        var entry = Parse(text);

        Assert.Equal(new TimeOnly(10, 0), entry.Start);
        Assert.Equal("打合せ", entry.Title);
        Assert.True(entry.CanCommit);
    }

    [Fact]
    public void 題や場所の全角文字は半角にしない()
    {
        var entry = Parse("打合せ　＠会議室Ａ");

        Assert.Equal("打合せ", entry.Title);
        Assert.Equal("会議室Ａ", entry.Location);
    }

    [Fact]
    public void 桁あふれる数字でも例外にならず日付エラーになる()
    {
        var entry = Parse("99999999999日 x");

        Assert.True(entry.HasDateError);
        Assert.False(entry.CanCommit);
    }
}
