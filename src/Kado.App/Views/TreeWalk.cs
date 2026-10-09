using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Kado.App.Views;

/// <summary>
/// マウスで押された要素から親をたどる。
/// <para>
/// <c>e.OriginalSource</c> は Visual とは限らない。<c>TextBlock</c> の中の <c>Run</c>
/// （題名の後ろの「↻」など）を押すと、<c>Run</c> そのものが来る。<c>Run</c> は
/// <see cref="ContentElement"/> なので、<see cref="VisualTreeHelper.GetParent"/> に渡すと
/// 「Visual または Visual3D ではありません」の例外になり、アプリごと落ちていた。
/// Visual でないものは論理ツリーの親（<c>Run</c> なら <c>TextBlock</c>）へ上がり、
/// そこから先はふつうに Visual の親をたどる。
/// </para>
/// </summary>
internal static class TreeWalk
{
    /// <summary>親。Visual なら Visual の親、それ以外は論理ツリーの親。無ければ null。</summary>
    public static DependencyObject? ParentOf(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node),
    };
}
