using System.Windows;
using System.Windows.Controls;

namespace Kado.App.Views;

/// <summary>
/// 予定の題名の右に出す印（地球儀・クリップ）。見た目は XAML、判断は ViewModel
/// （<c>HasLink</c>／<c>HasAttachments</c>）にある。
/// </summary>
public partial class EventMarks : UserControl
{
    public static readonly DependencyProperty MarkSizeProperty = DependencyProperty.Register(
        nameof(MarkSize), typeof(double), typeof(EventMarks), new PropertyMetadata(11.0));

    public EventMarks() => InitializeComponent();

    /// <summary>印1つの大きさ（px）。題名の文字に合わせる。月のチップは 11、一覧・右パネルは 12。</summary>
    public double MarkSize
    {
        get => (double)GetValue(MarkSizeProperty);
        set => SetValue(MarkSizeProperty, value);
    }
}
