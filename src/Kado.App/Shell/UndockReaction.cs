using Kado.Presentation.Settings;
using Kado.Presentation.ViewModels;

namespace Kado.App.Shell;

/// <summary>
/// AppBar の登録が外れたとき（<c>AppBarHost.Undocked</c>）の受け方と、終了時の片付け。
/// <para>
/// <see cref="ShellController"/> は <c>Window</c> を握っていて Linux のテストに載せられない。
/// 「どの外れ方なら居かたを書き換えるか」と「終了時に印を必ず消す」の判断だけを、
/// WPF にも Win32 にも触れないこの型へ切り出してある。
/// </para>
/// </summary>
internal sealed class UndockReaction(ShellViewModel shell, Action<bool> markReserved)
{
    private bool _shuttingDown;

    /// <summary>
    /// 全画面アプリや OS 側の都合で外れたとき、見た目（居かた）も合わせる。
    /// <para>
    /// <b>終了処理の途中で来たものは、使う人がピンを外したのではない。</b>
    /// ここで Overlay に書き換えると、次の起動でピンが外れてスライドになる。
    /// </para>
    /// </summary>
    public void OnUndocked()
    {
        if (_shuttingDown) return;

        if (shell.IsPinned) shell.Mode = ShellMode.Overlay;
    }

    /// <summary>
    /// 終了する。AppBar を外し（作業領域は Windows へ返る）、そのあとで終了印を消す。
    /// <para>
    /// 居かた（<see cref="ShellViewModel.Mode"/>）は書き換えない。以前は外れた通知が
    /// Overlay への書き換えを起こし、その <c>ModeChanged</c> が印を消していた。
    /// 書き換えを止めたので、印は<b>ここで明示的に</b>消す。消さないと、正常終了でも
    /// 次の起動が異常終了と見て全モニタのワークエリアを送り直してしまう。
    /// </para>
    /// <para>
    /// 印を消すのは外したあと（<c>AppBarHost.Undock</c> と同じ順序）。途中で落ちれば
    /// 印が残り、次の起動で戻せる。何度呼んでも安全。
    /// </para>
    /// </summary>
    /// <param name="removeAppBar">AppBar を外す処理（<c>AppBarHost.Dispose</c>）。</param>
    public void Shutdown(Action removeAppBar)
    {
        _shuttingDown = true;

        removeAppBar();
        markReserved(false);
    }
}
