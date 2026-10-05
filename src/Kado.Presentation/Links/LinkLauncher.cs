using System.ComponentModel;
using Kado.Data.Models;

namespace Kado.Presentation.Links;

/// <summary>開いた結果。</summary>
/// <param name="Opened">何かを起動したか。</param>
/// <param name="Message">使う人に出す文言。無ければ null（黙って開けた）。</param>
public sealed record LaunchResult(bool Opened, string? Message)
{
    /// <summary>黙って開けた。</summary>
    public static readonly LaunchResult Success = new(true, null);

    /// <summary>開けなかった。理由を文言にして返す。</summary>
    public static LaunchResult Failed(string message) => new(false, message);
}

/// <summary>
/// URL・予定の添付・タスクのファイルの場所を開く。
/// <para>
/// 予定の編集画面・タスクの編集画面・右クリックメニューのどれもここを通る。<b>何を開いてよいかの
/// 安全確認は <see cref="LinkRules"/> と <see cref="PathLaunchPlanner"/> が持ち</b>、ここはそれを
/// 呼んで、通ったものだけを <see cref="ILinkOpener"/> に渡す。開けなかった理由は文言にして返すので、
/// 呼び出し側は ViewModel の表示に出すだけでよい（例外は返さない）。
/// </para>
/// </summary>
/// <param name="opener">実際の起動。</param>
/// <param name="fileExists">ファイルがあるか。渡さなければ <see cref="File.Exists"/>。</param>
/// <param name="directoryExists">フォルダがあるか。渡さなければ <see cref="Directory.Exists"/>。</param>
public sealed class LinkLauncher(
    ILinkOpener opener, Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
{
    private readonly ILinkOpener _opener = opener ?? throw new ArgumentNullException(nameof(opener));
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

    /// <summary>何も起動しない。組み立て方を指定されなかった編集画面などが使う。</summary>
    public static LinkLauncher None { get; } = new(NullLinkOpener.Instance);

    /// <summary>URL を既定のブラウザで開く。<b>http と https だけ</b>。</summary>
    public LaunchResult OpenWeb(string? url)
    {
        if (LinkRules.WebUrl(url) is not { } safe)
        {
            return LaunchResult.Failed("このリンクは開けません（http または https の URL ではありません）");
        }

        return Launch(new LaunchRequest(LaunchKind.Shell, safe));
    }

    /// <summary>予定の添付（Google ドライブのファイル）を既定のブラウザで開く。<b>https だけ</b>。</summary>
    public LaunchResult OpenAttachment(EventAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (LinkRules.SecureUrl(attachment.FileUrl) is not { } safe)
        {
            return LaunchResult.Failed("この添付は開けません（https の URL ではありません）");
        }

        return Launch(new LaunchRequest(LaunchKind.Shell, safe));
    }

    /// <summary>
    /// タスクに添えたファイル・フォルダを開く。見つからなければ開かない。実行形式は実行せず、
    /// 入っているフォルダを開いてそのファイルを選んだ状態にする（<see cref="PathLaunchPlanner"/>）。
    /// <para>
    /// <b>存在の確認は別のスレッドで行う。</b>共有フォルダ（<c>\\server\share</c>）の先が落ちていると、
    /// <c>File.Exists</c> が数十秒返らないことがあり、画面のスレッドで呼ぶと本体ごと固まる。
    /// 起動は呼んだ側のスレッドに戻ってから行う（シェルの起動は画面のスレッドが安全）。
    /// </para>
    /// </summary>
    public async Task<LaunchResult> OpenPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        var plan = await Task.Run(
                () => PathLaunchPlanner.Plan(path, _fileExists, _directoryExists), cancellationToken)
            .ConfigureAwait(true);

        if (plan.Request is not { } request) return LaunchResult.Failed(plan.Message ?? "開けませんでした");

        var result = Launch(request);

        // 実行しなかったことを伝える。開けたので成功だが、文言は残す
        return result.Opened && plan.Message is not null ? new LaunchResult(true, plan.Message) : result;
    }

    private LaunchResult Launch(LaunchRequest request)
    {
        try
        {
            _opener.Launch(request);
            return LaunchResult.Success;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
            or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return LaunchResult.Failed($"開けませんでした（{ex.Message}）");
        }
    }
}
