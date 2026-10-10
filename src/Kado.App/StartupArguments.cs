namespace Kado.App;

/// <summary>
/// 起動の引数。WPF にも Windows API にも触れない純粋な部分で、Linux で試験できる。
/// </summary>
internal static class StartupArguments
{
    /// <summary>
    /// 入れ替え直後の起動だと新しいほうへ伝える合図（<c>UpdateService.AfterUpdateArgument</c> と同じ値）。
    /// </summary>
    internal const string AfterUpdate = "--after-update";

    /// <summary>
    /// 自分で立ち上げ直したあと（復元・壊れたデータの退避・失敗後のやり直し・シャットダウンの取り消し）の合図。
    /// </summary>
    internal const string AfterRestart = "--after-restart";

    /// <summary>
    /// 窓を出さずに、トレイに入った状態で始めるよう伝える合図（<c>UpdateService.KeepHiddenArgument</c> と同じ値）。
    /// 常駐中の自動更新で、トレイに入っていた窓を、再起動のたびに前へ出さないため。
    /// </summary>
    internal const string KeepHidden = "--keep-hidden";

    /// <summary>前のプロセスが終わるのを待つ長さ。</summary>
    internal static readonly TimeSpan PreviousProcessWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 二重起動の判定の前に、前のプロセスが終わるのを待つべきか。
    /// <para>
    /// 待たずに判定すると、前のプロセスがまだミューテックスを持っていて「すでに起動しています」で
    /// 即座に終わり、立ち上げ直したのに起動しないように見える。
    /// </para>
    /// </summary>
    internal static bool WaitsForPreviousProcess(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, AfterUpdate, StringComparison.Ordinal)
                      || string.Equals(a, AfterRestart, StringComparison.Ordinal));

    /// <summary>窓を隠したまま始めるよう伝えられているか。</summary>
    internal static bool KeepsHidden(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, KeepHidden, StringComparison.Ordinal));
}
