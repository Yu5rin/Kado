using System.Windows;

namespace Kado.App.Views;

/// <summary>
/// ドラッグの最中かを数える。自動更新が、ドラッグの途中で入れ替えて再起動しないための目印。
/// <para>
/// <c>DragDrop.DoDragDrop</c> は終わるまで戻らない（中でメッセージを回すので、タイマーは動き続ける）。
/// 呼び出しを必ずここ経由にして、前後で数える。
/// </para>
/// </summary>
internal static class DragActivity
{
    private static int _active;

    /// <summary>いまドラッグの最中か。</summary>
    internal static bool IsDragging => Volatile.Read(ref _active) > 0;

    /// <summary><c>DragDrop.DoDragDrop</c> を、最中であることを数えながら呼ぶ。</summary>
    internal static DragDropEffects DoDragDrop(
        DependencyObject source, object data, DragDropEffects allowedEffects)
    {
        Interlocked.Increment(ref _active);

        try
        {
            return DragDrop.DoDragDrop(source, data, allowedEffects);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
