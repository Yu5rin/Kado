namespace Kado.App.Shell;

/// <summary>
/// トレイまわりの判断。WPF にも Win32 にも触れない純粋な部分だけを置き、Linux で試験できる。
/// </summary>
internal static class TrayPolicy
{
    /// <summary>
    /// 起動時にアイコンを置けなかったとき、次にやり直すまでの間隔。
    /// <para>
    /// ログオン直後はタスクバー（Explorer）がまだ通知領域を用意できていないことがある。
    /// 2秒・5秒・15秒・30秒と延ばし、それ以降は60秒ごと。諦めはしない
    /// （置けるまで、アプリの窓を隠さずに待つ）。
    /// </para>
    /// </summary>
    internal static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        4 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// 閉じるボタンで窓を隠してよいか。
    /// <para>
    /// <b>トレイにアイコンが出ていないときは隠さない。</b>隠すと、戻る入口（トレイのアイコン）が
    /// どこにも無く、窓を呼び戻せない。そのまま閉じる（終了する）ほうが、
    /// 見えない常駐を残すよりよい。
    /// </para>
    /// </summary>
    /// <param name="reallyExiting">トレイのメニューの「終了」など、終わると決めているか。</param>
    /// <param name="trayShown">トレイにアイコンが出ているか。</param>
    /// <param name="closeToTrayEnabled">設定で「閉じるボタンはトレイへ」が有効か。</param>
    internal static bool HidesOnClose(bool reallyExiting, bool trayShown, bool closeToTrayEnabled) =>
        !reallyExiting && trayShown && closeToTrayEnabled;
}
