namespace Kado.Presentation.Editing;

/// <summary>
/// 元に戻す・やり直しの履歴。
/// <para>
/// 新しい編集を実行すると、それまでの「やり直し」は捨てる。分岐した履歴を持てるように
/// すると、どちらへ戻るのかが利用者に予測できなくなるため。
/// </para>
/// </summary>
public sealed class UndoStack
{
    /// <summary>覚えておく編集の数。これを超えた古いものから捨てる。</summary>
    public const int Capacity = 100;

    private readonly LinkedList<IUndoableEdit> _done = [];
    private readonly Stack<IUndoableEdit> _undone = new();

    /// <summary>履歴が変わったときに呼ばれる。</summary>
    public event EventHandler? Changed;

    /// <summary>元に戻せるか。</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>やり直せるか。</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>次に元に戻す編集の説明。無ければ null。</summary>
    public string? UndoDescription => _done.Last?.Value.Description;

    /// <summary>次にやり直す編集の説明。無ければ null。</summary>
    public string? RedoDescription => _undone.Count > 0 ? _undone.Peek().Description : null;

    /// <summary>編集を実行し、履歴に積む。</summary>
    public void Execute(IUndoableEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        edit.Apply();

        _done.AddLast(edit);
        // 新しい枝に進んだので、古い枝のやり直しは意味を失う
        _undone.Clear();

        while (_done.Count > Capacity) _done.RemoveFirst();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>直前の編集を元に戻す。</summary>
    /// <returns>戻した編集の説明。戻すものが無ければ null。</returns>
    public string? Undo()
    {
        if (_done.Last is not { } node) return null;

        _done.RemoveLast();
        node.Value.Revert();
        _undone.Push(node.Value);

        Changed?.Invoke(this, EventArgs.Empty);
        return node.Value.Description;
    }

    /// <summary>元に戻した編集をやり直す。</summary>
    /// <returns>やり直した編集の説明。やり直すものが無ければ null。</returns>
    public string? Redo()
    {
        if (_undone.Count == 0) return null;

        var edit = _undone.Pop();
        edit.Apply();
        _done.AddLast(edit);

        Changed?.Invoke(this, EventArgs.Empty);
        return edit.Description;
    }

    /// <summary>履歴を捨てる。データを読み直したときなど、過去の編集が意味を失う場面で呼ぶ。</summary>
    public void Clear()
    {
        if (_done.Count == 0 && _undone.Count == 0) return;

        _done.Clear();
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
