using Kado.Presentation.Editing;

namespace Kado.Presentation.Tests;

/// <summary>
/// 繰り返しの選択肢の文言。
/// <para>
/// こちらの5択に当てはまらない指定（Google で作った「隔週の月水金」など）は
/// 「このまま」の1項目になる。以前は「この予定の設定のまま」としか出ず、
/// 繰り返しなのかどうかも読めなかった。
/// </para>
/// </summary>
public class RecurrenceChoiceTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private static string CustomLabelFor(string? spec) =>
        RecurrenceChoice.OptionsFor(Monday, includeCustom: true, spec)
            .Single(o => o.Kind == RecurrenceKind.Custom).Label;

    [Fact]
    public void 読めた指定は中身を文言に出す()
    {
        var label = CustomLabelFor("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR");

        Assert.Contains("このまま", label, StringComparison.Ordinal);
        Assert.Contains("月", label, StringComparison.Ordinal);
        Assert.Contains("水", label, StringComparison.Ordinal);
        Assert.Contains("金", label, StringComparison.Ordinal);
        Assert.NotEqual("この予定の設定のまま", label);
    }

    [Fact]
    public void 終了日のある指定もそのまま読める()
    {
        var label = CustomLabelFor("FREQ=MONTHLY;BYMONTHDAY=-1;UNTIL=20261231T000000Z");

        Assert.Contains("このまま", label, StringComparison.Ordinal);
        Assert.NotEqual("この予定の設定のまま", label);
    }

    [Fact]
    public void 読めない指定は元の言い方に戻る()
    {
        Assert.Equal("この予定の設定のまま", CustomLabelFor("さっぱり読めない"));
        Assert.Equal("この予定の設定のまま", CustomLabelFor(null));
        Assert.Equal("この予定の設定のまま", CustomLabelFor(string.Empty));
    }

    [Fact]
    public void 元が5択に収まるなら_このまま_は出さない()
    {
        Assert.DoesNotContain(
            RecurrenceChoice.OptionsFor(Monday, includeCustom: false),
            o => o.Kind == RecurrenceKind.Custom);
    }
}
