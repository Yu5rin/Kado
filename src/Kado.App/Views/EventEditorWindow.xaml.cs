using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.Infrastructure;

namespace Kado.App.Views;

/// <summary>予定の編集画面。入力は <see cref="EventEditorViewModel"/> が持つ。</summary>
public partial class EventEditorWindow : Window
{
    /// <summary>上下キーとホイールで動かす幅。時間割は15分単位で考えることが多い。</summary>
    private const int NudgeMinutes = 15;

    private readonly EventEditorViewModel _editor;

    public EventEditorWindow(EventEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = _editor = editor;

        SaveCommand = new RelayCommand(Save, () => editor.CanSave);
        DeleteCommand = new RelayCommand(Delete, () => !editor.IsNew);

        // RelayCommand は CommandManager に乗っていないので、自分で知らせないと
        // 「保存できるようになったのにボタンが戻らない」状態のままになる
        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>保存して閉じる。キャンセルは「キャンセル」ボタンの IsCancel が受ける。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>削除を求めて閉じる。既存の予定を編集しているときだけボタンを出す。</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>
    /// 保存の先頭で、いま打ちかけの値を確定させる。
    /// <para>
    /// 「保存」は IsDefault なので、Enter で押すとフォーカスを動かさずに閉じる。
    /// 時刻欄は LostFocus で確定するバインディングなので、放っておくと打った値が
    /// ViewModel に届く前に閉じてしまう。
    /// </para>
    /// </summary>
    private void Save()
    {
        CommitFocusedBinding();

        // 確定した値で保存できるかをもう一度確かめる。無効なまま閉じない
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
        switch (Keyboard.FocusedElement)
        {
            case TextBox box: box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); break;
            case ComboBox box: box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource(); break;
        }
    }

    /// <summary>上下キーで15分ずつ動かす。打ち直すより速い。</summary>
    /// <summary>
    /// 一覧から選んだら、その場で伝える。
    /// <para>
    /// 時刻の欄は打ち込みの途中で整形されないよう、フォーカスが外れるまで
    /// 伝えない作りにしてある。選んだときまで待たせると、開始を選んでも
    /// 終了が動かないように見える。
    /// </para>
    /// </summary>
    private void OnTimeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box) return;

        // 終了の候補は「10:00（1時間）」と長さを添えて並べている。
        // 欄に入れるのは時刻のほうだけ
        if (box.SelectedItem is EndTimeOption option) box.Text = option.Time;

        box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    private void OnTimeKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Up => NudgeMinutes,
            Key.Down => -NudgeMinutes,
            _ => 0,
        };

        // 一覧が開いているときは、上下で候補を選ぶほうが自然
        if (step == 0 || sender is not ComboBox { IsDropDownOpen: false } box) return;

        Nudge(box, step);
        e.Handled = true;
    }

    /// <summary>ホイールでも同じだけ動かす。</summary>
    private void OnTimeWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false, IsKeyboardFocusWithin: true } box) return;

        Nudge(box, e.Delta > 0 ? NudgeMinutes : -NudgeMinutes);
        e.Handled = true;
    }

    private void Nudge(ComboBox box, int minutes)
    {
        // 打ちかけの文字を先に取り込んでから動かす。動かした結果が打った値を無視しないように
        box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

        // 単日と複数日で欄が分かれるので、名前ではなく印で見分ける
        if (box.Tag as string == "End") _editor.NudgeEnd(minutes);
        else _editor.NudgeStart(minutes);
    }

    // ------------------------------------------------------------------
    // 添付
    // ------------------------------------------------------------------

    /// <summary>ファイルを選んでドライブへ上げる。上げ終わるまでボタンは押せなくなる（IsUploadingAttachment）。</summary>
    private async void OnAddAttachmentClick(object sender, RoutedEventArgs e) => await _editor.AddAttachmentAsync();

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EventAttachment attachment }) _editor.RemoveAttachment(attachment);
    }

    /// <summary>行を押すと既定のブラウザで開く。https の添付だけ（EventEditorViewModel.IsSafeToOpen）。</summary>
    private void OnOpenAttachmentClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EventAttachment attachment }) _editor.OpenAttachment(attachment);
    }
}
