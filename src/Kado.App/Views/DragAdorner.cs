using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Kado.App.Views;

/// <summary>
/// ドラッグ中、掴んでいるものをカーソルに付けて見せる。
/// <para>
/// WPF の標準のドラッグはカーソルの形が変わるだけで、何を掴んでいるのかが見えない。
/// 掴んだ要素の見た目をそのまま写して、マウスに付いて回らせる。
/// </para>
/// <para>
/// 飾り（Adorner）は本体の上に重ねて描かれる層なので、置いても画面の配置は動かない。
/// </para>
/// </summary>
internal sealed class DragAdorner : Adorner
{
    private readonly Brush _face;
    private readonly Size _size;

    private Point _position;

    private DragAdorner(UIElement layer, UIElement source) : base(layer)
    {
        // 掴んでいるものの下に落とし先が見えるよう、少し透かす
        _face = new VisualBrush(source) { Opacity = 0.85 };
        _size = source.RenderSize;

        // 掴んでいるものが当たり判定を奪うと、落とし先を拾えなくなる
        IsHitTestVisible = false;
    }

    /// <summary>
    /// 掴んだ要素の姿を、その画面の飾り層に置く。
    /// <para>置けないときは null。飾り層はウィンドウが組み上がる前には無い。</para>
    /// </summary>
    public static DragAdorner? Attach(UIElement source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (Window.GetWindow(source)?.Content is not UIElement layerOwner) return null;
        if (AdornerLayer.GetAdornerLayer(layerOwner) is not { } layer) return null;
        if (source.RenderSize is { Width: <= 0 } or { Height: <= 0 }) return null;

        var adorner = new DragAdorner(layerOwner, source);
        layer.Add(adorner);
        return adorner;
    }

    /// <summary>掴んでいるものを、いまのマウス位置へ動かす。</summary>
    /// <param name="position">飾り層から見たマウスの位置。</param>
    public void MoveTo(Point position)
    {
        // つまんだ指の先ではなく、少し右下にずらして置く。真下だと落とし先が隠れる
        _position = new Point(position.X + 10, position.Y + 6);
        InvalidateVisual();
    }

    /// <summary>飾り層から外す。ドラッグが終わったら必ず呼ぶ。</summary>
    public void Detach() => (Parent as AdornerLayer)?.Remove(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        drawingContext.DrawRoundedRectangle(_face, null, new Rect(_position, _size), 3, 3);
    }
}
