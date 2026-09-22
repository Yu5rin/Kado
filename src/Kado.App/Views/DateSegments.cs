using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Presentation.Infrastructure;

namespace Kado.App.Views;

/// <summary>
/// 日付欄を「年・月・日」の区切りで扱えるようにする。
/// <para>
/// 押したところの区切りをまるごと選ぶので、そのまま打てば置き換わる。上下キーで
/// 1つずつ増減、左右キーで隣の区切りへ移る。<b>打ち直すたびに全部消して入れ直す、
/// という手間を無くす。</b>
/// </para>
/// <para>
/// <c>DatePicker</c> の中の入力欄はテンプレートの奥にあり、XAML からは直に掴めない。
/// 添付プロパティにして、スタイルから付ける。
/// </para>
/// </summary>
internal static class DateSegments
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(DateSegments),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject target, bool value) =>
        target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBox box) return;

        if (e.NewValue is true)
        {
            box.PreviewMouseLeftButtonUp += OnClicked;
            box.GotKeyboardFocus += OnFocused;
            box.PreviewKeyDown += OnKeyDown;
            return;
        }

        box.PreviewMouseLeftButtonUp -= OnClicked;
        box.GotKeyboardFocus -= OnFocused;
        box.PreviewKeyDown -= OnKeyDown;
    }

    /// <summary>押したところの区切りを選ぶ。</summary>
    private static void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;

        // 自分で範囲を引いたなら、そちらを優先する
        if (box.SelectionLength > 1) return;

        SelectAt(box, box.CaretIndex);
    }

    /// <summary>手が渡ったら、いちばん左の区切りから始める。</summary>
    private static void OnFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) SelectAt(box, 0);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Text is not { Length: > 0 }) return;

        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                if (Step(box, e.Key == Key.Up ? 1 : -1)) e.Handled = true;
                break;

            case Key.Left:
                if (Move(box, -1)) e.Handled = true;
                break;

            case Key.Right:
                if (Move(box, 1)) e.Handled = true;
                break;
        }
    }

    /// <summary>いま選んでいる区切りの数を増減する。</summary>
    private static bool Step(TextBox box, int by)
    {
        var (from, length) = SpanAt(box.Text, box.SelectionStart);

        if (length <= 0) return false;
        if (!int.TryParse(box.Text.AsSpan(from, length), out var value)) return false;

        var text = (value + by).ToString(System.Globalization.CultureInfo.InvariantCulture)
            .PadLeft(length, '0');

        box.Text = string.Concat(box.Text.AsSpan(0, from), text, box.Text.AsSpan(from + length));
        box.Select(from, text.Length);

        return true;
    }

    /// <summary>隣の区切りへ移る。端まで来たら、そこで止める。</summary>
    private static bool Move(TextBox box, int by)
    {
        var (from, length) = SpanAt(box.Text, box.SelectionStart);

        if (length <= 0) return false;

        var next = by > 0 ? from + length + 1 : from - 2;

        if (next < 0 || next >= box.Text.Length) return false;

        SelectAt(box, next);
        return true;
    }

    private static void SelectAt(TextBox box, int caret)
    {
        var (from, length) = SpanAt(box.Text, caret);

        if (length > 0) box.Select(from, length);
    }

    private static (int From, int Length) SpanAt(string? text, int caret) =>
        DateSegment.At(text, caret);
}
