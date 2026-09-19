using System.Windows;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.App.Views;

/// <summary>タスクの編集画面。入力は <see cref="TaskEditorViewModel"/> が持つ。</summary>
public partial class TaskEditorWindow : Window
{
    public TaskEditorWindow(TaskEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = editor;

        SaveCommand = new RelayCommand(() => DialogResult = true, () => editor.CanSave);
        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    public RelayCommand SaveCommand { get; }
}
