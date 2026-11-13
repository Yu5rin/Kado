using System.Windows;
using System.Windows.Input;

namespace Kado.App.Views;

/// <summary>
/// 予定・タスク・カレンダーの編集画面に共通の、閉じ方とキー。
/// <list type="bullet">
/// <item>Esc・キャンセル・×で閉じるとき、開いたときから変えていれば「変更を破棄しますか？」と尋ねる。
/// 変えていなければ尋ねない。保存・削除で閉じるときも尋ねない。</item>
/// <item>Ctrl＋Enter で保存する。メモ欄（<c>AcceptsReturn</c>）にいても効く。
/// Enter は改行に使われるので、トンネル側（<c>PreviewKeyDown</c>）で先に受ける。</item>
/// </list>
/// </summary>
internal static class EditorWindowGuard
{
    /// <param name="window">編集画面。<c>ShowDialog</c> で出すもの。</param>
    /// <param name="isDirty">開いたときから変えたか（各 ViewModel の <c>IsDirty</c>）。</param>
    /// <param name="isDeleted">削除を求めて閉じようとしているか。削除のときは尋ねない。</param>
    /// <param name="save">保存を試みる。保存できなければ何もしない（各画面の <c>Save</c>）。</param>
    internal static void Attach(Window window, Func<bool> isDirty, Func<bool> isDeleted, Action save)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;

            save();
            e.Handled = true;
        };

        window.Closing += (_, e) =>
        {
            if (e.Cancel) return;

            // 保存して閉じる（DialogResult が true）・削除を求めて閉じるときは、尋ねない
            if (window.DialogResult == true || isDeleted()) return;

            if (!isDirty()) return;

            // 既定のボタンは「編集に戻る」。誤って Enter を押しても、入力を捨てない
            if (!ConfirmWindow.Ask(window, "変更を破棄しますか？", "入力した内容は保存されません。", "破棄する", "編集に戻る"))
            {
                e.Cancel = true;
            }
        };
    }
}
