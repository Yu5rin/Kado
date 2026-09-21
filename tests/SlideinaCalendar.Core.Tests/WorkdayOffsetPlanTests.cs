using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Tests;

/// <summary>工程逆算のオフセット列（項目1）の、設定への出し入れ。</summary>
public class WorkdayOffsetPlanTests
{
    [Fact]
    public void 一度も保存していないときは既定の1セットを返す()
    {
        var plans = WorkdayOffsetPlanStore.Read(null);

        var plan = Assert.Single(plans);
        Assert.Equal("標準", plan.Name);
        Assert.Equal(
            [("仕様期限", -12), ("1次GO", -9), ("S中日程", -5), ("M中日程", -2)],
            plan.Steps.Select(s => (s.Name, s.Offset)));
    }

    [Fact]
    public void 空文字も一度も保存していない扱い()
    {
        Assert.Single(WorkdayOffsetPlanStore.Read(string.Empty));
    }

    [Fact]
    public void 書いて読み直すと同じ内容になる()
    {
        var plans = new[]
        {
            new WorkdayOffsetPlan("p1", "量産品", [new WorkdayOffsetStep("仕様期限", -12), new WorkdayOffsetStep("M中", -2)]),
            new WorkdayOffsetPlan("p2", "試作品", [new WorkdayOffsetStep("着手", 0)]),
        };

        var json = WorkdayOffsetPlanStore.Write(plans);
        var loaded = WorkdayOffsetPlanStore.Read(json);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("p1", loaded[0].Id);
        Assert.Equal("量産品", loaded[0].Name);
        Assert.Equal(-12, loaded[0].Steps[0].Offset);
        Assert.Equal("p2", loaded[1].Id);
        Assert.Equal(0, loaded[1].Steps[0].Offset);
    }

    [Fact]
    public void 空配列として保存したものは空のまま返す()
    {
        var json = WorkdayOffsetPlanStore.Write([]);

        Assert.Empty(WorkdayOffsetPlanStore.Read(json));
    }

    [Fact]
    public void 壊れたJSONは既定の1セットへ倒す()
    {
        var plans = WorkdayOffsetPlanStore.Read("{ これはJSONとして壊れている");

        Assert.Single(plans);
    }
}
