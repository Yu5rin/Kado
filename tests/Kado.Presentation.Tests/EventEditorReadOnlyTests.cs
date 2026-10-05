using Kado.Data.Models;
using Kado.Presentation.Editing;

namespace Kado.Presentation.Tests;

/// <summary>
/// 予定の編集画面の読み取り専用モード（右クリックの「詳細を見る」）。
/// 読み取り専用のカレンダー・Google 側で変えられない予定は、保存しても向こうへ伝わらない。
/// こちらだけ食い違わないよう、欄は触れず、保存も削除もできない。
/// </summary>
public class EventEditorReadOnlyTests
{
    private static readonly SourceChoice[] Calendars = [new("cal-a", "仕事")];

    private static CalendarEvent Stored() => new()
    {
        Id = "e1", Title = "打ち合わせ", Date = new DateOnly(2026, 10, 6),
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0), CalendarId = "cal-a",
    };

    [Fact]
    public void 読み取り専用では保存も削除もできず_理由と見出しと閉じるを出す()
    {
        var editor = new EventEditorViewModel(Stored(), Calendars, readOnlyReason: "Google 側で作られた予定です");

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.IsEditable);
        Assert.False(editor.CanSave);
        Assert.False(editor.CanDelete);
        Assert.Equal("Google 側で作られた予定です", editor.ReadOnlyReason);
        Assert.Equal("予定の詳細", editor.HeaderText);
        Assert.Equal("閉じる", editor.CancelLabel);

        // 中身は読める
        Assert.Equal("打ち合わせ", editor.Title);
        Assert.Equal("10:00", editor.StartTimeText);

        Assert.Throws<InvalidOperationException>(editor.ToModel);

        editor.RequestDelete();
        Assert.False(editor.Deleted);
    }

    [Fact]
    public void 読み取り専用でなければこれまでどおり()
    {
        var editor = new EventEditorViewModel(Stored(), Calendars);

        Assert.False(editor.IsReadOnly);
        Assert.True(editor.IsEditable);
        Assert.True(editor.CanSave);
        Assert.True(editor.CanDelete);
        Assert.Null(editor.ReadOnlyReason);
        Assert.Equal("予定の編集", editor.HeaderText);
        Assert.Equal("キャンセル", editor.CancelLabel);
        Assert.Equal("打ち合わせ", editor.ToModel().Title);

        editor.RequestDelete();
        Assert.True(editor.Deleted);
    }

    [Fact]
    public void 新しい予定には削除のボタンを出さない()
    {
        var editor = new EventEditorViewModel(new DateOnly(2026, 10, 6), Calendars);

        Assert.False(editor.CanDelete);
        Assert.Equal("予定の追加", editor.HeaderText);
        Assert.Equal("キャンセル", editor.CancelLabel);
    }
}
