using System.Windows;
using System.Windows.Controls;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.App.Views;

/// <summary>タスクの編集画面。入力は <see cref="TaskEditorViewModel"/> が持つ。</summary>
public partial class TaskEditorWindow : Window
{
    private readonly TaskEditorViewModel _editor;

    public TaskEditorWindow(TaskEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = _editor = editor;

        SaveCommand = new RelayCommand(() => DialogResult = true, () => editor.CanSave);
        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>「今日」「明日」「来週」。期限が付いていなければ一緒に付ける。</summary>
    private void OnDuePresetClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DateOnly date) _editor.SetDue(date);
    }
}
