using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Kado.App.Converters;

namespace Kado.App.Themes;

/// <summary>
/// 配色を当て直したあとに、コンバーターが引いたブラシをもう一度引かせる。
/// <para>
/// <see cref="ThemeManager.Apply"/> は辞書ごと作り直すので、<c>DynamicResource</c> で結んだ色は
/// 追随する。ところがコンバーターが <c>TryFindResource</c> で引いたブラシ（マイルストーンのラベル、
/// 期限の強調色、同期の丸印、色を持たないカレンダーの既定色）は、バインドの元の値が変わらない限り
/// 再評価されず、<b>前の配色のまま残る</b>。
/// </para>
/// <para>
/// そこで、開いている窓の木をたどり、<see cref="IThemeSensitiveConverter"/> を使っているバインドだけ
/// <c>UpdateTarget</c> で結び直す。要素を作り直さないので、スクロールの位置や選択は保たれる。
/// 木の途中で何かが起きても、ほかの要素の更新は続ける（配色の切り替えで落とさない）。
/// </para>
/// </summary>
internal static class ThemeBindingRefresh
{
    /// <summary>開いているすべての窓に適用する。</summary>
    public static void RefreshAll()
    {
        if (Application.Current is not { } app) return;

        foreach (Window window in app.Windows) Refresh(window);
    }

    /// <summary>1つの木のバインドを結び直す。</summary>
    internal static void Refresh(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current)) continue;

            RefreshOwnBindings(current);

            // 見た目の木（テンプレートから作られた要素もここに居る）。
            // Visual でないもの（Run など）は子の数を聞けない
            if (current is Visual or Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);

                for (var i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(current, i));
            }

            // 論理の木。見た目の木に載らない子（ContentElement など）を拾う
            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject element) pending.Push(element);
            }
        }
    }

    /// <summary>この要素自身に直に付いているバインドのうち、配色に依るコンバーターのものを結び直す。</summary>
    private static void RefreshOwnBindings(DependencyObject element)
    {
        // 列挙は写しを返すので、結び直しで値が動いても壊れない
        var values = element.GetLocalValueEnumerator();

        while (values.MoveNext())
        {
            if (values.Current.Value is not BindingExpression { ParentBinding.Converter: IThemeSensitiveConverter } expression)
            {
                continue;
            }

            try
            {
                expression.UpdateTarget();
            }
            catch (InvalidOperationException)
            {
                // 元が解けていない（外れかけの要素など）。そこだけ諦める
            }
        }
    }
}
