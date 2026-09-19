using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 色見本が選ばれているかを返す。
/// <para>選んだ色は ViewModel が1つだけ持つので、見本ごとに突き合わせる。</para>
/// </summary>
public sealed class ColorChoiceIsSelectedConverter : IValueConverter
{
    /// <summary>いま選ばれている色。画面を開くたびに差し替える。</summary>
    public string? Selected { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string color && string.Equals(color, Selected, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>カレンダーとタスクリストの編集画面。</summary>
public partial class CalendarEditorWindow : Window
{
    private readonly ColorChoiceIsSelectedConverter _isSelected = new();

    public CalendarEditorWindow(CalendarEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        // 色見本の判定は XAML を読む前に用意する。テンプレートから参照されるため
        _isSelected.Selected = editor.Color;
        Resources.Add("ColorChoiceIsSelected", _isSelected);

        InitializeComponent();
        DataContext = editor;

        SaveCommand = new RelayCommand(() => DialogResult = true, () => editor.CanSave);
        PickColorCommand = new RelayCommand<string?>(color =>
        {
            if (color is not null) editor.Color = color;
        });

        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>保存して閉じる。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>色見本を押したとき。</summary>
    public RelayCommand<string?> PickColorCommand { get; }
}
