using SlideinaCalendar.Presentation.Editing;

namespace SlideinaCalendar.Presentation.Tests;

public class UndoStackTests
{
    /// <summary>適用と巻き戻しの回数を数えるだけの編集。</summary>
    private sealed class CountingEdit(string description = "テスト編集") : IUndoableEdit
    {
        public string Description { get; } = description;
        public int Applied { get; private set; }
        public int Reverted { get; private set; }

        public void Apply() => Applied++;
        public void Revert() => Reverted++;
    }

    [Fact]
    public void 実行すると適用され履歴に積まれる()
    {
        var stack = new UndoStack();
        var edit = new CountingEdit();

        stack.Execute(edit);

        Assert.Equal(1, edit.Applied);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal("テスト編集", stack.UndoDescription);
    }

    [Fact]
    public void 元に戻すと巻き戻る()
    {
        var stack = new UndoStack();
        var edit = new CountingEdit();
        stack.Execute(edit);

        var description = stack.Undo();

        Assert.Equal("テスト編集", description);
        Assert.Equal(1, edit.Reverted);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void やり直すと再び適用される()
    {
        var stack = new UndoStack();
        var edit = new CountingEdit();
        stack.Execute(edit);
        stack.Undo();

        stack.Redo();

        Assert.Equal(2, edit.Applied);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void 新しい編集をするとやり直しは捨てられる()
    {
        var stack = new UndoStack();
        stack.Execute(new CountingEdit("1つ目"));
        stack.Undo();

        // ここで別の編集をすると履歴が分岐する。どちらへ戻るのか予測できなくなるので
        // 古い枝は捨てる
        stack.Execute(new CountingEdit("2つ目"));

        Assert.False(stack.CanRedo);
        Assert.Equal("2つ目", stack.UndoDescription);
    }

    [Fact]
    public void 戻すものが無ければ何も起きない()
    {
        var stack = new UndoStack();

        Assert.Null(stack.Undo());
        Assert.Null(stack.Redo());
        Assert.Null(stack.UndoDescription);
        Assert.Null(stack.RedoDescription);
    }

    [Fact]
    public void 複数の編集を順に戻せる()
    {
        var stack = new UndoStack();
        var first = new CountingEdit("1つ目");
        var second = new CountingEdit("2つ目");

        stack.Execute(first);
        stack.Execute(second);

        Assert.Equal("2つ目", stack.Undo());
        Assert.Equal("1つ目", stack.Undo());
        Assert.False(stack.CanUndo);

        // やり直しは戻した順の逆
        Assert.Equal("1つ目", stack.Redo());
        Assert.Equal("2つ目", stack.Redo());
    }

    [Fact]
    public void 上限を超えると古いものから捨てられる()
    {
        var stack = new UndoStack();

        for (var i = 0; i <= UndoStack.Capacity; i++) stack.Execute(new CountingEdit($"編集{i}"));

        // 上限ぶんだけ戻れる。溜め込み続けるとメモリを圧迫する
        var count = 0;
        while (stack.Undo() is not null) count++;

        Assert.Equal(UndoStack.Capacity, count);
    }

    [Fact]
    public void 履歴を捨てられる()
    {
        var stack = new UndoStack();
        stack.Execute(new CountingEdit());
        stack.Undo();

        stack.Clear();

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void 変化したときに通知される()
    {
        var stack = new UndoStack();
        var notified = 0;
        stack.Changed += (_, _) => notified++;

        stack.Execute(new CountingEdit());
        stack.Undo();
        stack.Redo();
        stack.Clear();

        Assert.Equal(4, notified);
    }

    [Fact]
    public void 何も無いときのクリアでは通知されない()
    {
        var stack = new UndoStack();
        var notified = 0;
        stack.Changed += (_, _) => notified++;

        stack.Clear();

        Assert.Equal(0, notified);
    }
}
