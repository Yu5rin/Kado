using System.Windows;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.App.Views;

/// <summary>予定の編集画面。入力は <see cref="EventEditorViewModel"/> が持つ。</summary>
public partial class EventEditorWindow : Window
{
    public EventEditorWindow(EventEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = editor;

        SaveCommand = new RelayCommand(() => DialogResult = true, () => editor.CanSave);

        // RelayCommand は CommandManager に乗っていないので、自分で知らせないと
        // 「保存できるようになったのにボタンが戻らない」状態のままになる
        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>保存して閉じる。取り消しは「取り消し」ボタンの IsCancel が受ける。</summary>
    public RelayCommand SaveCommand { get; }
}
