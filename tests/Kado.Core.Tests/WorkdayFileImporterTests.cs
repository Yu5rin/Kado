using Kado.Core.Import;
using Kado.Core.WorkingDays;

namespace Kado.Core.Tests;

/// <summary>
/// 配布ファイルと<b>同じ形</b>の xlsx を読む統合テスト。
/// <para>
/// 列の位置（B=稼働日／C=通し番号／D=マイルストーン）とヘッダの作りが変わったときに
/// 気付けるようにしてある。モックではなく実際に xlsx を開いて読む。
/// </para>
/// <para>
/// 中身は<b>合成</b>。以前は会社配布の実ファイルを置いていたが、作成者の氏名・社内の
/// ファイルサーバのパス・部署名・外部リンク先が埋め込まれていた。リポジトリを公開すると
/// それらも公開されるので、同じ構造の合成データに差し替えた。
/// </para>
/// </summary>
public class WorkdayFileImporterTests
{
    private static readonly string SampleFilePath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "実働日サンプル.xlsx");

    private static ImportResult ImportSample()
    {
        using var stream = File.OpenRead(SampleFilePath);
        return new WorkdayFileImporter().Import(stream);
    }

    [Fact]
    public void サンプルが配置されている()
        => Assert.True(File.Exists(SampleFilePath), $"テストデータが見つかりません: {SampleFilePath}");

    [Fact]
    public void 稼働日753件とマイルストーン176件を読める()
    {
        var result = ImportSample();

        Assert.Equal(753, result.WorkingDays.Count);
        Assert.Equal(176, result.Milestones.Count);
    }

    [Fact]
    public void 対象期間は稼働日とマイルストーンで異なる()
    {
        var result = ImportSample();

        Assert.Equal(new DateOnly(2023, 1, 5), result.WorkingDayRangeStart);
        Assert.Equal(new DateOnly(2025, 11, 24), result.WorkingDayRangeEnd);
        Assert.Equal(new DateOnly(2025, 1, 10), result.MilestoneRangeStart);
        Assert.Equal(new DateOnly(2025, 11, 12), result.MilestoneRangeEnd);
    }

    [Fact]
    public void バージョンを読める()
        => Assert.Equal("Vｅｒ．26.1", ImportSample().Version);

    [Fact]
    public void 読み込みで警告は出ない()
    {
        // 5行目の見出しを警告にしないこと（データが始まる前の非日付セルは黙って読み飛ばす）
        var result = ImportSample();
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void マイルストーン名称は決め打ちせずファイルの文字列をそのまま登録する()
    {
        var names = ImportSample().MilestoneNames;

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
        var byName = ImportSample().Milestones
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(44, byName["仕様期限"]);
        Assert.Equal(44, byName["1次GO"]);
        Assert.Equal(44, byName["S中日程"]);
        Assert.Equal(44, byName["M中日程"]);
    }

    [Fact]
    public void マイルストーンには取り込み元バージョンが入る()
        => Assert.All(ImportSample().Milestones, m => Assert.Equal("Vｅｒ．26.1", m.SourceVersion));

    [Fact]
    public void 稼働日は昇順で重複が無い()
    {
        var days = ImportSample().WorkingDays;

        Assert.Equal(days.Count, days.Distinct().Count());
        Assert.Equal(days.Order().ToArray(), days.ToArray());
    }

    [Fact]
    public void C列の通し番号は読まずアプリ側で数え直す()
    {
        // Excel の C 列は 2023/1/5 を 1 とする通し番号。アプリは「その月の何実働日目か」を使うので
        // ファイルの値をそのまま持たず、月ごとに数え直していることを確認する。
        var calendar = ImportSample().ToCalendar();

        Assert.Equal(1, calendar.IndexInMonth(new DateOnly(2023, 1, 5)));
        Assert.Equal(1, calendar.IndexInMonth(new DateOnly(2023, 2, 1)));   // 2月も 1 から数え直す
    }

    [Fact]
    public void 取り込み結果からカレンダーを組める()
    {
        var calendar = ImportSample().ToCalendar();

        Assert.Equal(753, calendar.Count);
        Assert.True(calendar.IsWorkingDay(new DateOnly(2023, 1, 5)));
        Assert.False(calendar.IsWorkingDay(new DateOnly(2023, 1, 7)));   // 土曜
        Assert.True(calendar.HasDataFor(new DateOnly(2024, 6, 1)));
        Assert.False(calendar.HasDataFor(new DateOnly(2026, 4, 1)));     // 範囲外
        Assert.Equal("仕様期限", Assert.Single(calendar.MilestonesOn(new DateOnly(2025, 1, 10))).Name);
    }

    [Fact]
    public void 翌年度の期限は暦日にフォールバックする()
    {
        // 実データの終わりは 2026/3/31。その先は実働日で数えられないので暦日になる
        var formatter = new DueDateFormatter(ImportSample().ToCalendar());

        var result = formatter.Format(due: new DateOnly(2026, 4, 30), today: new DateOnly(2026, 3, 2));

        Assert.Equal(DueKind.CalendarDays, result.Kind);
        Assert.Equal("残り 59日", result.Text);
    }

    [Fact]
    public void ストリームを二度読みしても同じ結果になる()
    {
        var first = ImportSample();
        var second = ImportSample();

        Assert.Equal(first.WorkingDays, second.WorkingDays);
        Assert.Equal(first.Version, second.Version);
    }
}
