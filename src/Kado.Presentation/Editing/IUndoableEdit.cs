namespace Kado.Presentation.Editing;

/// <summary>
/// やり直しのきく編集。
/// <para>
/// <see cref="Apply"/> と <see cref="Revert"/> は何度呼ばれても同じ結果になること。
/// Undo と Redo を往復するたびに呼ばれるため、状態を内部に溜めると食い違う。
/// </para>
/// <para>
/// 持ってよいのは、<b>直前の <see cref="Apply"/>／<see cref="Revert"/> が実際に何を変えたか</b>
/// （消した行の姿、元に戻したかどうか）だけ。Google と同期した後の姿は、編集の間に
/// 同期が書き換えているので、書き込む時点の最新の行を読んで決める
/// （<see cref="SyncLinks"/>）。
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
