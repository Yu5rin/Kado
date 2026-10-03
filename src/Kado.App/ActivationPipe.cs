namespace Kado.App;

/// <summary>
/// 「前に出ろ」の合図を送る名前付きパイプの、名前と、待ち受けのやり直しの間隔。
/// <para>
/// WPF にも Windows API にも触れない純粋な計算だけを置いてあり、Linux で試験できる。
/// </para>
/// </summary>
internal static class ActivationPipe
{
    /// <summary>名前の前半。</summary>
    internal const string Prefix = "Kado.Activate";

    /// <summary>
    /// パイプの名前。<b>セッション ID とユーザーの SID を含める。</b>
    /// <para>
    /// ミューテックスは <c>Local\</c> でセッション単位なのに、パイプ名は「マシン全体」で共有される。
    /// 共有 PC やリモートデスクトップで2人が同時に使うと、一方のパイプを他方が使えず
    /// （または他方の Kado へ合図が飛び）、2本目の起動が前に出てこない。
    /// </para>
    /// </summary>
    internal static string NameFor(int sessionId, string? userSid) =>
        $"{Prefix}.{sessionId}.{Sanitize(userSid)}";

    /// <summary>名前に使える文字（英数字・ハイフン・下線）だけにする。無ければ <c>unknown</c>。</summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var chars = value.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>
    /// 待ち受けが失敗したあと、やり直すまで置く間隔。連続して失敗するほど延ばす（上限 5 秒）。
    /// <para>
    /// 間を置かずに回すと、パイプを作れない状態（別のプロセスが同じ名前を使っている、など）で
    /// CPU を使い切る。
    /// </para>
    /// </summary>
    internal static TimeSpan RetryDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;

        var milliseconds = 250.0 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 5));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, 5000));
    }
}
