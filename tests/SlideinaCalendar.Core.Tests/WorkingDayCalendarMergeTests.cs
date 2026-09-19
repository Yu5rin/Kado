using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

/// <summary>
/// 取り込み時の「既存データは全置換せず、ファイルに含まれる期間だけを置き換える」規則の検証。
/// <para>
/// 古いファイルを誤って読み込んでも過去データが消えないようにするための規則であり、
/// 稼働日とマイルストーンで期間が異なる点まで含めて担保する（要件書 4.1）。
/// </para>
/// </summary>
public class WorkingDayCalendarMergeTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>
    /// 既存データ。稼働日は 2022/12〜2023/6、マイルストーンは 2022/12〜2025/3 に散らしてある。
    /// </summary>
    private static WorkingDayCalendar Existing() => WorkingDayCalendar.Create(
        [D(2022, 12, 1), D(2023, 1, 5), D(2023, 1, 6), D(2023, 6, 1)],
        D(2022, 12, 1), D(2023, 6, 1),
        [
            new Milestone(D(2022, 12, 1), "旧A"),
            new Milestone(D(2025, 1, 15), "旧B"),
            new Milestone(D(2025, 3, 1), "旧C"),
        ],
        D(2022, 12, 1), D(2025, 3, 1));

    /// <summary>
    /// 取り込むファイル。稼働日は 2023/1/5〜2023/3/31、マイルストーンは 2025/1/10〜2025/1/20。
    /// 両者の期間がずれているのが実ファイルと同じ状況。
    /// </summary>
    private static ImportResult Incoming() => new(
        Version: "Ver．26.1",
        WorkingDayRangeStart: D(2023, 1, 5),
        WorkingDayRangeEnd: D(2023, 3, 31),
        MilestoneRangeStart: D(2025, 1, 10),
        MilestoneRangeEnd: D(2025, 1, 20),
        WorkingDays: [D(2023, 1, 5), D(2023, 2, 1)],
        Milestones: [new Milestone(D(2025, 1, 15), "新B", "Ver．26.1")],
        Warnings: []);

    [Fact]
    public void ファイルの期間外にある既存の稼働日は残る()
    {
        var merged = Existing().Merge(Incoming());

        Assert.True(merged.IsWorkingDay(D(2022, 12, 1)));   // 期間より前
        Assert.True(merged.IsWorkingDay(D(2023, 6, 1)));    // 期間より後
    }

    [Fact]
    public void ファイルの期間内にある既存の稼働日は置き換わる()
    {
        var merged = Existing().Merge(Incoming());

        Assert.True(merged.IsWorkingDay(D(2023, 1, 5)));    // ファイルにも入っている
        Assert.False(merged.IsWorkingDay(D(2023, 1, 6)));   // ファイルに無いので消える
        Assert.True(merged.IsWorkingDay(D(2023, 2, 1)));    // ファイルで追加された
    }

    [Fact]
    public void 稼働日の総数が期間単位の置き換えと一致する()
    {
        // 残る 2件（2022/12/1・2023/6/1）＋ ファイルの 2件
        Assert.Equal(4, Existing().Merge(Incoming()).Count);
    }

    [Fact]
    public void マイルストーンは稼働日とは別の期間で置き換える()
    {
        var merged = Existing().Merge(Incoming());

        // 2025/1/15 はマイルストーン期間の内側なので置き換わる
        Assert.Equal("新B", Assert.Single(merged.MilestonesOn(D(2025, 1, 15))).Name);

        // 期間の外側は残る
        Assert.Equal("旧A", Assert.Single(merged.MilestonesOn(D(2022, 12, 1))).Name);
        Assert.Equal("旧C", Assert.Single(merged.MilestonesOn(D(2025, 3, 1))).Name);
    }

    [Fact]
    public void 稼働日の期間で消えるのは稼働日だけでマイルストーンは巻き添えにしない()
    {
        // 稼働日の期間（2023/1/5〜2023/3/31）にマイルストーンがあっても、
        // マイルストーンの期間外なら消えないこと
        var existing = WorkingDayCalendar.Create(
            [D(2023, 1, 5)],
            D(2023, 1, 1), D(2023, 12, 31),
            [new Milestone(D(2023, 2, 10), "稼働日期間内の旧マイルストーン")],
            D(2023, 2, 1), D(2023, 2, 28));

        var merged = existing.Merge(Incoming());

        Assert.Single(merged.MilestonesOn(D(2023, 2, 10)));
    }

    [Fact]
    public void 登録範囲は広がるが狭まらない()
    {
        var merged = Existing().Merge(Incoming());

        Assert.Equal(D(2022, 12, 1), merged.RangeStart);
        Assert.Equal(D(2023, 6, 1), merged.RangeEnd);          // 既存の方が広いので維持
        Assert.Equal(D(2022, 12, 1), merged.MilestoneRangeStart);
        Assert.Equal(D(2025, 3, 1), merged.MilestoneRangeEnd);
    }

    [Fact]
    public void 空のカレンダーに取り込むとファイルの内容だけになる()
    {
        var merged = WorkingDayCalendar.Empty.Merge(Incoming());

        Assert.Equal(2, merged.Count);
        Assert.Equal(D(2023, 1, 5), merged.RangeStart);
        Assert.Equal(D(2023, 3, 31), merged.RangeEnd);
        Assert.Single(merged.AllMilestones);
    }

    [Fact]
    public void マイルストーンが無いファイルは既存のマイルストーンに触らない()
    {
        var incoming = Incoming() with
        {
            MilestoneRangeStart = null,
            MilestoneRangeEnd = null,
            Milestones = [],
        };

        var merged = Existing().Merge(incoming);

        Assert.Equal(3, merged.AllMilestones.Count);
    }

    [Fact]
    public void 古いファイルを読み直しても新しい期間のデータは消えない()
    {
        // 2026年分まで入ったカレンダーに、2023年分しか無い古いファイルを流し込む
        var current = WorkingDayCalendar.Create(
            [D(2023, 1, 5), D(2026, 3, 31)],
            D(2023, 1, 5), D(2026, 3, 31),
            null, null, null);

        var merged = current.Merge(Incoming());

        Assert.True(merged.IsWorkingDay(D(2026, 3, 31)));
        Assert.Equal(D(2026, 3, 31), merged.RangeEnd);
    }

    [Fact]
    public void 取り込んだファイルを既存データに重ねても期間外は残る()
    {
        using var stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "TestData", "実働日サンプル.xlsx"));
        var result = new WorkdayFileImporter().Import(stream);

        // ファイルの期間（2023/1/5〜2025/11/24）の外側にある古いデータ
        var existing = WorkingDayCalendar.Create(
            [D(2022, 12, 28), D(2023, 5, 1)],
            D(2022, 12, 28), D(2023, 5, 1),
            null, null, null);

        var merged = existing.Merge(result);

        Assert.True(merged.IsWorkingDay(D(2022, 12, 28)));           // 期間外なので残る
        Assert.Equal(754, merged.Count);                              // 753 + 残った 1 件
        Assert.Equal(D(2022, 12, 28), merged.RangeStart);
        Assert.Equal(D(2025, 11, 24), merged.RangeEnd);
        Assert.Equal(176, merged.AllMilestones.Count);
    }
}
