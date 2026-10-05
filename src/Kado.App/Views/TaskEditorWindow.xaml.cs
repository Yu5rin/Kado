using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Data.Models;
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
        RecreateCommand = new RelayCommand(Recreate, () => editor.IsMissingOnGoogle && editor.CanSave);

        editor.PropertyChanged += (_, _) =>
        {
            SaveCommand.RaiseCanExecuteChanged();
            RecreateCommand.RaiseCanExecuteChanged();
        };

        Loaded += (_, _) => TitleBox.Focus();
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>削除を求めて閉じる。既存のタスクを編集しているときだけボタンを出す。</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>
    /// 「Google に新しく作り直す」。保存と一緒に、結び付きを外して新規として送る指定を残す。
    /// 「Google 上で見つからない」印が付いたタスクにだけボタンを出す。
    /// </summary>
    public RelayCommand RecreateCommand { get; }

    /// <summary>作り直しを ViewModel に求めて、保存して閉じる。</summary>
    private void Recreate()
    {
        _editor.RequestRecreate();
        Save();
    }

    /// <summary>「今日」「明日」「来週」。期限が付いていなければ一緒に付ける。</summary>
    private void OnDuePresetClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DateOnly date) _editor.SetDue(date);
    }

    // ------------------------------------------------------------------
    // 添付（ファイルの場所）。Kado だけが持ち、Google には送らない
    // ------------------------------------------------------------------

    private void OnAddFilesClick(object sender, RoutedEventArgs e) => _editor.AddFiles();

    private void OnAddFoldersClick(object sender, RoutedEventArgs e) => _editor.AddFolders();

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TaskAttachment attachment }) _editor.RemoveAttachment(attachment);
    }

    /// <summary>
    /// 行を押すとその場所を開く。見つからない・開けないときは、画面の下に理由が出る。
    /// <para>async void から例外が漏れるとアプリごと終わるので、ここが最後の砦（docs/README.md）。</para>
    /// </summary>
    private async void OnOpenAttachmentClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskAttachment attachment }) return;

        try
        {
            await _editor.OpenAttachmentAsync(attachment);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _editor.ReportAttachmentFailure(ex);
        }
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
