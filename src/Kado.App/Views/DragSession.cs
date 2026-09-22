using System.Windows;
using System.Windows.Input;
using Kado.Data.Models;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>
/// 掴んで落とすまでの一連。月・週・日のどのビューからでも同じ動きにする。
/// <para>
/// 一度に動かせるのは1つなので、状態はひとつだけ持つ。ビューをまたいで
/// 掴んだまま移れる（週ビューの終日レーンから時間軸へ、など）。
/// </para>
/// </summary>
internal sealed class DragSession
{
    /// <summary>いまのドラッグ。</summary>
    public static DragSession Current { get; } = new();

    private Point _origin;
    private object? _candidate;
    private DragAdorner? _adorner;

    private DragSession() { }

    /// <summary>
    /// 押した場所と、押されたものを控える。
    /// <para>押しただけでは動かさない。動かすかどうかは <see cref="DragIfMoved"/> が決める。</para>
    /// </summary>
    public void Press(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ClickCount != 1) return;

        _origin = e.GetPosition(null);
        _candidate = (sender as FrameworkElement)?.DataContext;
    }

    /// <summary>
    /// 押したまま動かしたらドラッグを始める。
    /// <para>
    /// すぐに始めると、選ぼうとしただけの押し込みまで拾う。OS が決めている
    /// 最小の移動量を超えてからにする。
    /// </para>
    /// </summary>
    public void DragIfMoved(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.LeftButton != MouseButtonState.Pressed || _candidate is null) return;
        if (sender is not UIElement source) return;

        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _origin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var moved = _candidate;
        _candidate = null;

        // 元は薄く残し、掴んだほうをカーソルに付ける。どこから来て、いま何を
        // 掴んでいるのかが同時に見える
        var opacity = source.Opacity;
        _adorner = DragAdorner.Attach(source);
        source.Opacity = 0.35;

        try
        {
            DragDrop.DoDragDrop(source, moved, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            End();
            source.Opacity = opacity;
        }
    }

    /// <summary>掴んでいるものをマウスに追わせる。</summary>
    public void Follow(DragEventArgs e, UIElement reference)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_adorner is null) return;

        var root = Window.GetWindow(reference)?.Content as UIElement ?? reference;
        _adorner.MoveTo(e.GetPosition(root));
    }

    /// <summary>掴んでいるものを消す。落ちたときと、途中でやめたとき。</summary>
    public void End()
    {
        _adorner?.Detach();
        _adorner = null;
    }

    /// <summary>掴んでいるものを取り出す。予定・日付の行・タスクのどれでもなければ null。</summary>
    public static object? Payload(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        foreach (var kind in Kinds)
        {
            if (e.Data.GetDataPresent(kind)) return e.Data.GetData(kind);
        }

        return null;
    }

    /// <summary>Ctrl を押しながらなら複製。</summary>
    public static bool IsCopy(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
    }

    /// <summary>落とせるかどうかをカーソルで示す。</summary>
    public static void ShowEffect(DragEventArgs e, bool canDrop)
    {
        ArgumentNullException.ThrowIfNull(e);

        e.Effects = canDrop
            ? IsCopy(e) ? DragDropEffects.Copy : DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static readonly Type[] Kinds =
        [typeof(EventChipViewModel), typeof(MilestoneViewModel), typeof(TimeBlockViewModel), typeof(TaskItem)];
}
