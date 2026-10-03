using System.ComponentModel;
using System.IO;
using Kado.Presentation.Update;

namespace Kado.App.Update;

/// <summary>exe の入れ替えの結果の種類。</summary>
internal enum SwapOutcome
{
    /// <summary>入れ替えて、新しいほうを起動した。呼んだ側はすぐ終わること。</summary>
    Swapped,

    /// <summary>入れ替える前に止まった。ファイルは元のまま。</summary>
    FailedUnchanged,

    /// <summary>入れ替えたあとで止まった（新しい exe を起動できなかった、など）。元の版へ戻した。</summary>
    RolledBack,

    /// <summary>入れ替えたあとで止まり、元に戻すのにも失敗した。手で戻してもらうしかない。</summary>
    RollbackFailed,
}

/// <summary>入れ替えの結果。利用者に出す文言も持つ（例外の型名は出さない）。</summary>
internal sealed record SwapResult(SwapOutcome Outcome, Exception? Error, string Current, string Backup)
{
    public bool Succeeded => Outcome == SwapOutcome.Swapped;

    /// <summary>
    /// 失敗したときに画面へ出す文。<b>「ダウンロードできなかった」とは言わない。</b>
    /// 落とせたあとの入れ替えの失敗なので、原因も次の一手も別。
    /// </summary>
    public string Message => Outcome switch
    {
        SwapOutcome.Swapped => string.Empty,

        SwapOutcome.FailedUnchanged =>
            $"入れ替えられませんでした。元の版のままです（{Reason(Error)}）。"
            + "リリースのページから手で差し替えてください。",

        SwapOutcome.RolledBack =>
            $"新しい版を入れましたが、起動できませんでした（{Reason(Error)}）。元の版に戻しました。"
            + "リリースのページから手で差し替えるか、管理者にご確認ください。",

        SwapOutcome.RollbackFailed =>
            "入れ替えに失敗し、元の版へ戻すこともできませんでした。"
            + $"{Backup} を {Current} に名前を変えて戻してください。",

        _ => string.Empty,
    };

    /// <summary>止まった理由を、利用者が手掛かりにできる言葉にする。</summary>
    internal static string Reason(Exception? error) => error switch
    {
        // AppLocker・ウイルス対策・SmartScreen・グループポリシーで、実行を止められた
        Win32Exception => "セキュリティ ソフトや管理者の設定に実行を止められた可能性があります",
        UnauthorizedAccessException => "ファイルを置き換える権限がありません",
        IOException => "ファイルを置き換えられませんでした。使用中か、ディスクの空きが足りない可能性があります",
        _ => "理由は shell.log に残しました",
    };
}

/// <summary>
/// 実行中の exe を新しい版に入れ替える。
/// <para>
/// 実行中の exe は上書きできないが、<b>名前は変えられる</b>。そこで
/// 「いまの exe を <c>.old</c> へ改名 → 新しい exe を置く → 新しいほうを起動」の順で行う。
/// </para>
/// <para>
/// <b>新しい exe を起動できなかったときも巻き戻す。</b>AppLocker・ウイルス対策・SmartScreen に
/// 止められると <c>Process.Start</c> が <see cref="Win32Exception"/> になる。そのまま終わると、
/// 次から起動できない exe だけが残る。新しい exe を <c>.failed</c> へ退けて、<c>.old</c> を元の名前へ戻す。
/// </para>
/// <para>
/// WPF にも Windows API にも触れない（起動は <c>launch</c> として受け取る）ので、Linux で試験できる。
/// </para>
/// </summary>
internal static class ExecutableSwap
{
    internal const string OldSuffix = ".old";

    /// <summary>起動できなかった新しい exe の退避先の接尾辞。</summary>
    internal const string FailedSuffix = ".failed";

    /// <param name="current">いま動いている exe。</param>
    /// <param name="downloaded">落とした新しい exe。入れ替えに成功したら消す。</param>
    /// <param name="launch">入れ替えた exe を起動する。失敗したら例外を投げる。</param>
    /// <param name="log">段階を残す先。</param>
    internal static SwapResult Run(
        string current, string downloaded, Action<string> launch, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloaded);
        ArgumentNullException.ThrowIfNull(launch);

        log ??= _ => { };

        var backup = current + OldSuffix;
        var failed = current + FailedSuffix;
        var renamed = false;

        try
        {
            // 前回の入れ替えで残ったものを先に片付ける
            TryDelete(backup);
            TryDelete(failed);

            // 実行中の exe は上書きできないが、名前は変えられる
            File.Move(current, backup);
            renamed = true;

            File.Copy(downloaded, current, overwrite: true);

            launch(current);

            TryDelete(downloaded);

            log("更新の入れ替え: 新しい版に入れ替えて起動しました");
            return new SwapResult(SwapOutcome.Swapped, null, current, backup);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"更新の入れ替え: 失敗。{UpdateDiagnostics.Summarize(ex)}");

            // 名前を変える前に転んだなら、何も動いていない
            if (!renamed) return new SwapResult(SwapOutcome.FailedUnchanged, ex, current, backup);

            try
            {
                SetAsideNewExecutable(current, failed);
                File.Move(backup, current);

                log("更新の入れ替え: 元の版に戻しました");
                return new SwapResult(SwapOutcome.RolledBack, ex, current, backup);
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            {
                log($"更新の入れ替え: 元に戻すのにも失敗。{UpdateDiagnostics.Summarize(rollback)}。" +
                    $"{backup} を {current} に手で戻してください");

                return new SwapResult(SwapOutcome.RollbackFailed, ex, current, backup);
            }
        }
    }

    /// <summary>
    /// 置いた新しい exe を、元の名前の場所から退ける。
    /// <para>
    /// 消さずに改名するのは、ウイルス対策ソフトが掴んでいても名前は変えられることが多いため。
    /// 改名できなければ消す。起動を止めたソフトが先に隔離して、もう無いこともある。
    /// </para>
    /// </summary>
    private static void SetAsideNewExecutable(string current, string failed)
    {
        if (!File.Exists(current)) return;

        try
        {
            File.Move(current, failed, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(current);
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても進める
        }
    }
}
