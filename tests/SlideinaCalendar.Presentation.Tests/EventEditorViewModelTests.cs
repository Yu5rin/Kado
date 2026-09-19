using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Tests;

public class EventEditorViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static EventEditorViewModel New() => new(D(2026, 9, 24), ["仕事"]);

    [Fact]
    public void タイトルが空なら保存できない()
    {
        var vm = New();

        Assert.False(vm.CanSave);
        Assert.Equal("タイトルを入れてください。", vm.ValidationMessage);

        vm.Title = "会議";
        Assert.True(vm.CanSave);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void 読めない時刻は保存できない()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "きゅうじ";

        Assert.False(vm.CanSave);
        Assert.Contains("開始時刻", vm.ValidationMessage);
    }

    [Fact]
    public void 終了が開始より前なら保存できない()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "13:00";
        vm.EndTimeText = "10:00";

        Assert.Contains("終了時刻", vm.ValidationMessage);
    }

    [Fact]
    public void 終日なら時刻は見ない()
    {
        var vm = New();
        vm.Title = "棚卸";
        vm.StartTimeText = "でたらめ";
        vm.IsAllDay = true;

        Assert.True(vm.CanSave);

        var model = vm.ToModel();
        Assert.Null(model.StartTime);
        Assert.Null(model.EndTime);
        Assert.True(model.IsAllDay);
    }

    [Fact]
    public void 日をまたぐ予定は時刻の逆転を許す()
    {
        var vm = New();
        vm.Title = "夜勤";
        vm.StartTimeText = "22:00";
        vm.EndTimeText = "06:00";

        // 単日なら打ち間違い
        Assert.False(vm.CanSave);

        vm.IsMultiDay = true;
        vm.EndDate = D(2026, 9, 25);

        Assert.True(vm.CanSave);
        Assert.Equal(D(2026, 9, 25), vm.ToModel().EndDate);
    }

    [Fact]
    public void 終了日を開始日より前にはできない()
    {
        var vm = New();
        vm.Title = "棚卸";
        vm.IsAllDay = true;
        vm.IsMultiDay = true;
        vm.EndDate = D(2026, 9, 20);

        Assert.Contains("終了日", vm.ValidationMessage);
    }

    [Fact]
    public void 開始日を後ろへ動かすと終了日も付いてくる()
    {
        var vm = New();
        vm.IsMultiDay = true;
        vm.EndDate = D(2026, 9, 25);

        vm.Date = D(2026, 9, 28);

        // 取り残して「終了日が前」の状態にしない
        Assert.Equal(D(2026, 9, 28), vm.EndDate);
    }

    [Fact]
    public void 既存を直しても識別子と繰り返しは残る()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
            Recurrence = "FREQ=WEEKLY", GoogleEventId = "g1", Source = "google",
        };

        var vm = new EventEditorViewModel(source, ["仕事"]);
        Assert.False(vm.IsNew);
        Assert.Equal("予定の編集", vm.HeaderText);
        Assert.Equal("09:00", vm.StartTimeText);

        vm.Title = "定例（変更）";
        var model = vm.ToModel();

        Assert.Equal("e1", model.Id);
        Assert.Equal("定例（変更）", model.Title);
        // 編集画面で触っていない項目を消さない
        Assert.Equal("FREQ=WEEKLY", model.Recurrence);
        Assert.Equal("g1", model.GoogleEventId);
        Assert.Equal("google", model.Source);
    }

    [Fact]
    public void 色は読み書きで往復する()
    {
        foreach (var accent in Enum.GetValues<EventAccent>())
        {
            var vm = New();
            vm.Title = "会議";
            vm.Accent = accent;

            var stored = vm.ToModel();
            var reopened = new EventEditorViewModel(stored, ["仕事"]);

            Assert.Equal(accent, reopened.Accent);
        }
    }

    [Fact]
    public void 空欄はnullにして保存する()
    {
        var vm = New();
        vm.Title = "  会議  ";
        vm.Location = "   ";
        vm.Note = "";

        var model = vm.ToModel();

        Assert.Equal("会議", model.Title);
        Assert.Null(model.Location);
        Assert.Null(model.Note);
    }

    [Fact]
    public void 保存できない状態でToModelを呼ぶと弾かれる()
    {
        Assert.Throws<InvalidOperationException>(() => New().ToModel());
    }
}
