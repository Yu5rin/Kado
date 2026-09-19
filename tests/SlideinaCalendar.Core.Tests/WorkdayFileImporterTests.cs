using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

/// <summary>
/// 会社配布の実ファイル（ツール用実働日.xlsx）を使った統合テスト。
/// モックではなく実物を読むことで、列の位置やヘッダの書式が変わったときに気付けるようにしている。
/// </summary>
public class WorkdayFileImporterTests
{
    private static readonly string RealFilePath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "ツール用実働日.xlsx");

    private static ImportResult ImportRealFile()
    {
        using var stream = File.OpenRead(RealFilePath);
        return new WorkdayFileImporter().Import(stream);
    }

    [Fact]
    public void 実ファイルが配置されている()
        => Assert.True(File.Exists(RealFilePath), $"テストデータが見つかりません: {RealFilePath}");

    [Fact]
    public void 実ファイルから稼働日753件とマイルストーン176件を読める()
    {
        var result = ImportRealFile();

        Assert.Equal(753, result.WorkingDays.Count);
        Assert.Equal(176, result.Milestones.Count);
    }

    [Fact]
    public void 実ファイルの対象期間は稼働日とマイルストーンで異なる()
    {
        var result = ImportRealFile();

        Assert.Equal(new DateOnly(2023, 1, 5), result.WorkingDayRangeStart);
        Assert.Equal(new DateOnly(2026, 3, 31), result.WorkingDayRangeEnd);
        Assert.Equal(new DateOnly(2025, 1, 10), result.MilestoneRangeStart);
        Assert.Equal(new DateOnly(2026, 3, 26), result.MilestoneRangeEnd);
    }

    [Fact]
    public void 実ファイルからバージョンを読める()
        => Assert.Equal("Vｅｒ．25.1", ImportRealFile().Version);

    [Fact]
    public void 実ファイルの読み込みで警告は出ない()
    {
        // 5行目の見出し「稲電稼働日」を警告にしないこと（ヘッダは黙って読み飛ばす）
        var result = ImportRealFile();
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void マイルストーン名称は決め打ちせずファイルの文字列をそのまま登録する()
    {
        var names = ImportRealFile().MilestoneNames;

        // 現行データは4種。この数や名前を実装側に埋め込んでいないことが要点で、
        // ファイルに5種目が現れれば自動的にここも増える。
        Assert.Equal(4, names.Count);
        Assert.Contains("仕様期限", names);
        Assert.Contains("1次GO", names);
        Assert.Contains("S中日程", names);
        Assert.Contains("M中日程", names);
    }

    [Fact]
    public void マイルストーンは4種が各44件ずつ入っている()
    {
        var byName = ImportRealFile().Milestones
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(44, byName["仕様期限"]);
        Assert.Equal(44, byName["1次GO"]);
        Assert.Equal(44, byName["S中日程"]);
        Assert.Equal(44, byName["M中日程"]);
    }

    [Fact]
    public void マイルストーンには取り込み元バージョンが入る()
        => Assert.All(ImportRealFile().Milestones, m => Assert.Equal("Vｅｒ．25.1", m.SourceVersion));

    [Fact]
    public void 稼働日は昇順で重複が無い()
    {
        var days = ImportRealFile().WorkingDays;

        Assert.Equal(days.Count, days.Distinct().Count());
        Assert.Equal(days.Order().ToArray(), days.ToArray());
    }

    [Fact]
    public void C列の通し番号は読まずアプリ側で数え直す()
    {
        // Excel の C 列は 2023/1/5 を 1 とする通し番号。アプリは「その月の何実働日目か」を使うので
        // ファイルの値をそのまま持たず、月ごとに数え直していることを確認する。
        var calendar = ImportRealFile().ToCalendar();

        Assert.Equal(1, calendar.IndexInMonth(new DateOnly(2023, 1, 5)));
        Assert.Equal(1, calendar.IndexInMonth(new DateOnly(2023, 2, 1)));   // 2月も 1 から数え直す
    }

    [Fact]
    public void 取り込み結果からカレンダーを組める()
    {
        var calendar = ImportRealFile().ToCalendar();

        Assert.Equal(753, calendar.Count);
        Assert.True(calendar.IsWorkingDay(new DateOnly(2023, 1, 5)));
        Assert.False(calendar.IsWorkingDay(new DateOnly(2023, 1, 7)));   // 土曜
        Assert.True(calendar.HasDataFor(new DateOnly(2024, 6, 1)));
        Assert.False(calendar.HasDataFor(new DateOnly(2026, 4, 1)));     // 翌年度は範囲外
        Assert.Equal("仕様期限", Assert.Single(calendar.MilestonesOn(new DateOnly(2025, 1, 10))).Name);
    }

    [Fact]
    public void 翌年度の期限は暦日にフォールバックする()
    {
        // 実データの終わりは 2026/3/31。その先は実働日で数えられないので暦日になる
        var formatter = new DueDateFormatter(ImportRealFile().ToCalendar());

        var result = formatter.Format(due: new DateOnly(2026, 4, 30), today: new DateOnly(2026, 3, 2));

        Assert.Equal(DueKind.CalendarDays, result.Kind);
        Assert.Equal("残り 59日", result.Text);
    }

    [Fact]
    public void ストリームを二度読みしても同じ結果になる()
    {
        var first = ImportRealFile();
        var second = ImportRealFile();

        Assert.Equal(first.WorkingDays, second.WorkingDays);
        Assert.Equal(first.Version, second.Version);
    }
}
