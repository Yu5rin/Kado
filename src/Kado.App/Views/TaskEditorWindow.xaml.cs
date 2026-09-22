using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Presentation.Editing;
using Kado.Presentation.Infrastructure;

namespace Kado.App.Views;

/// <summary>タスクの編集画面。入力は <see cref="TaskEditorViewModel"/> が持つ。</summary>
public partial class TaskEditorWindow : Window
{
    private readonly TaskEditorViewModel _editor;

    public TaskEditorWindow(TaskEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = _editor = editor;

        SaveCommand = new RelayCommand(Save, () => editor.CanSave);
        DeleteCommand = new RelayCommand(Delete, () => !editor.IsNew);

        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>削除を求めて閉じる。既存のタスクを編集しているときだけボタンを出す。</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>「今日」「明日」「来週」。期限が付いていなければ一緒に付ける。</summary>
    private void OnDuePresetClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DateOnly date) _editor.SetDue(date);
    }

    /// <summary>
    /// 保存の先頭で、いま打ちかけの値を確定させる。
    /// <para>この画面の欄はすべて PropertyChanged で確定するが、他の編集画面と形を揃えておく。</para>
    /// </summary>
    private void Save()
    {
        CommitFocusedBinding();

        if (!_editor.CanSave) return;

        DialogResult = true;
    }

    /// <summary>削除を ViewModel に求めて閉じる。実際の削除は呼び出し側が行う。</summary>
    private void Delete()
    {
        _editor.RequestDelete();
        DialogResult = false;
    }

    /// <summary>いまフォーカスしている要素の Text 系バインディングを確定させる。</summary>
    private static void CommitFocusedBinding()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
