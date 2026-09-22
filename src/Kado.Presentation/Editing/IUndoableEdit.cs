namespace Kado.Presentation.Editing;

/// <summary>
/// やり直しのきく編集。
/// <para>
/// <see cref="Apply"/> と <see cref="Revert"/> は何度呼ばれても同じ結果になること。
/// Undo と Redo を往復するたびに呼ばれるため、状態を内部に溜めると食い違う。
/// </para>
/// </summary>
public interface IUndoableEdit
{
    /// <summary>「予定を追加」など、元に戻すメニューに出す説明。</summary>
    string Description { get; }

    /// <summary>編集を適用する。</summary>
    void Apply();

    /// <summary>編集前の状態に戻す。</summary>
    void Revert();
}
