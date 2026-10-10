using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Kado.Core.Net;
using Kado.Presentation.Net;
using Kado.Presentation.Update;

namespace Kado.App.Update;

/// <summary>
/// GitHub Releases を見て、新しい版に入れ替える。
/// <para>
/// 通信するのは<b>起動したときと、利用者が押したとき</b>だけ。黙って裏で通信し続けない。
/// </para>
/// <para>
/// 実行中の exe は Windows が掴んでいて上書きできないが、<b>名前は変えられる</b>。
/// そこで「いまの exe を <c>.old</c> へ改名 → 新しい exe を置く → 新しいほうを起動 →
/// 自分は終わる」という順で入れ替える。途中で失敗したら改名を戻す。
/// </para>
/// <para>
/// 応答の読み取りと行き先の検査は <see cref="ReleaseFeed"/> が持つ。落としたものを
/// そのまま実行するので、判断の中身を試せるところへ置いてある。
/// </para>
/// </summary>
public sealed class UpdateService
{
    /// <summary>入れ替え直後の起動だと新しいほうへ伝える合図。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>
    /// 入れ替え直後は、前のプロセスが終わるのを待ってから二重起動を判定する。
    /// <para>待たないと「すでに起動しています」で即座に終わってしまう。</para>
    /// </summary>
    public static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(30);

    private const string OldSuffix = ExecutableSwap.OldSuffix;

    /// <summary>
    /// 落とすときの、読み取りが止まったとみなす長さ。
    /// <para>
    /// 全体の時間ではなく<b>アイドルタイムアウト</b>にしてある。70MB を遅い回線
    /// （120KB/s 未満）で落とすと数分かかり、全体に上限を掛けると毎回そこで切れて
    /// しまう。1バイトでも来ていれば、そのたびにここから数え直す。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Action<string> _log;
    private readonly UpdateChecker _checker;

    private int _checking;

    /// <param name="apiUrl">新しい版を見に行く API の URL。Atom フィードの URL はここから組み立てる。</param>
    /// <param name="log">更新の確認・ダウンロード・入れ替えの各段階を1行ずつ渡す先（shell.log）。</param>
    public UpdateService(string apiUrl, Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _checker = new UpdateChecker(apiUrl, CurrentVersion, CreateClient, _log);
    }

    /// <summary>いま確認中か。連打されても通信は1本に保つ。</summary>
    public bool IsChecking => Volatile.Read(ref _checking) != 0;

    /// <summary>いま動いているアプリの版。</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build)
            : new Version(0, 0, 0);

    /// <summary>
    /// 入れ替えの合図に付ける「窓は隠したままにする」印（<c>StartupArguments.KeepHidden</c> と同じ値）。
    /// 常駐中の自動更新が、トレイに入っていた窓を、再起動のたびに前へ出さないための印。
    /// </summary>
    public const string KeepHiddenArgument = "--keep-hidden";

    /// <summary>落としたものを置く場所（手で更新するとき。次の起動で片付ける）。</summary>
    private static string TempDir => Path.Combine(Path.GetTempPath(), "Kado", "update");

    /// <summary>
    /// 自動更新が落とした、入れ替え待ちのものを置く場所（データの保存先の <c>updates</c>）。
    /// <para>
    /// <see cref="TempDir"/> とは別。そちらは起動のたびに片付けるが、こちらは<b>次の起動でも残さなければ</b>
    /// ならない（終了したあとの起動で入れ替えるため）。片付けは <see cref="CleanupStaleStagedFiles"/>。
    /// </para>
    /// </summary>
    internal static string StagedDirectory =>
        Path.Combine(Path.GetDirectoryName(Kado.Data.CalendarDatabase.DefaultPath)!, "updates");

    /// <summary>
    /// 新しい版があるか見る。
    /// <para>
    /// 通信できなくても例外は投げず、<see cref="UpdateCheckStatus.Failed"/> を返すだけにする。
    /// 更新を確かめられないことは、アプリが使えない理由にはならない。
    /// </para>
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
        {
            _log("更新の確認: すでに確認中です");
            return UpdateCheckResult.AlreadyChecking();
        }

        try
        {
            // Atom を先に見て、新しい版があるときだけ API へ行く。流れは UpdateChecker。
            // 経路（プロキシ）を調べる処理は、自動設定の取得で数秒止まることがあるので、
            // 画面のスレッドから外して走らせる
            return await Task.Run(() => _checker.CheckAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>
    /// 通信を試す（設定の「うまく更新できないとき」）。
    /// <para>Atom・API・配布ファイルの置き場に接続し、先頭だけ受け取って切る。ファイルは保存しない。</para>
    /// </summary>
    public Task<ConnectionProbeReport> ProbeConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _checker.ProbeConnectionAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// 落とす。
    /// <para>ハッシュが付いていれば確かめる。合わなければ捨てて例外にする。</para>
    /// <para>
    /// <b>リダイレクトは自分で辿る。</b><c>AllowAutoRedirect</c> の既定（true）だと
    /// 最初の URL しか行き先を確かめず、途中で許されない場所へ跳ばされてもそのまま
    /// 辿って落としてしまう。行き先が変わるたびに <see cref="ReleaseFeed.IsAllowedDownloadUrl"/>
    /// を通す（<see cref="UpdateHttp.FollowAllowedRedirectsAsync"/>）。
    /// </para>
    /// <para>
    /// <b>ハッシュを確かめられない取得先（API を使わずに組み立てたもの）でも、</b>
    /// 中身が Web ページ（プロキシのエラーページなど）でないことと、先頭が実行ファイルの形
    /// （<c>MZ</c>）であることは、置く前に確かめる。落としたものはそのまま実行するので、
    /// エラーページを exe として置いてしまうと、起動できなくなる。
    /// </para>
    /// <para>
    /// 各段階を1行ずつ記録する。失敗したときは、例外の連鎖と受信済みバイト数を残す。
    /// </para>
    /// </summary>
    /// <param name="directory">置き場所。省くと <see cref="TempDir"/>（自動更新は <see cref="StagedDirectory"/> を渡す）。</param>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!ReleaseFeed.IsAllowedDownloadUrl(info.DownloadUrl))
        {
            _log($"更新のダウンロード: 取得先が許されていない場所のため中止 {UpdateDiagnostics.SafeUrl(info.DownloadUrl)}");
            throw new InvalidOperationException("更新の取得先が許されていない場所です。");
        }

        var folder = directory ?? TempDir;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Kado-{info.TagName}.exe");

        _log($"更新のダウンロード: 開始 {UpdateDiagnostics.SafeUrl(info.DownloadUrl)}" +
             $"（想定 {info.SizeBytes}バイト, SHA256={(info.Sha256 is { Length: > 0 } ? "あり" : "なし")}）");

        // 経路を調べる処理は、自動設定の取得で数秒止まることがある。画面のスレッドを止めない
        await Task.Run(() => _checker.LogNetworkEnvironmentOnce(info.DownloadUrl), CancellationToken.None)
            .ConfigureAwait(false);

        // 読み取りが止まったときだけ打ち切る。1バイトでも来るたびに、ここから数え直す
        using var idle = new CancellationTokenSource(DownloadIdleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        long done = 0;

        try
        {
            // AllowAutoRedirect は使わず、行き先を自分で確かめながら辿る
            using var http = CreateClient(allowAutoRedirect: false);

            using var response = await UpdateHttp
                .FollowAllowedRedirectsAsync(
                    http, info.DownloadUrl, "application/octet-stream", linked.Token,
                    m => _log("更新のダウンロード: " + m))
                .ConfigureAwait(false);

            // ヘッダを受け取れた。ここから改めて読み取りを見張り直す
            idle.CancelAfter(DownloadIdleTimeout);

            // 状態コード・Content-Type・転送先を残す。403/407 の区別と、中身がエラーページに
            // すり替わっていないか（text/html）は、会社の回線の切り分けに直結する
            _log("更新のダウンロード: " + UpdateDiagnostics.DescribeResponse(response));

            if (UpdateDiagnostics.DescribeRedirect(info.DownloadUrl, response) is { } redirect)
            {
                _log("更新のダウンロード: " + redirect);
            }

            UpdateHttp.EnsureSuccess(response);

            var contentType = response.Content.Headers.ContentType?.ToString();

            if (UpdateDiagnostics.IsHtml(contentType)) throw new UnexpectedContentException(contentType!);

            var total = response.Content.Headers.ContentLength ?? info.SizeBytes;

            await using (var source = await response.Content
                             .ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    // 来た。次のアイドルタイムアウトをまた最初から数える
                    idle.CancelAfter(DownloadIdleTimeout);

                    await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);

                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            // ハッシュを確かめられなくても、実行ファイルの形はしているか
            if (!await StartsWithExecutableHeaderAsync(path, linked.Token).ConfigureAwait(false))
            {
                throw new UnexpectedContentException(contentType ?? "(なし)");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が止めたのではなく、読み取りが1分止まって諦めた
            TryDelete(path);

            var timeout = new TimeoutException(
                $"更新の取得が{(int)DownloadIdleTimeout.TotalSeconds}秒間止まったので中止しました。");

            LogDownloadFailure(timeout, done, info);
            throw timeout;
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            _log($"更新のダウンロード: 中止された（受信済み {done}バイト）");
            throw;
        }
        catch (Exception ex)
        {
            // 書きかけを残さない。次に読むと壊れたものを掴む
            TryDelete(path);
            LogDownloadFailure(ex, done, info);
            throw;
        }

        _log($"更新のダウンロード: 受信を終えた（{done}バイト）");

        if (info.Sha256 is { Length: > 0 } expected)
        {
            // 70MB のハッシュ計算。画面のスレッドで行うと固まる
            var actual = await Task.Run(() => ComputeSha256(path), cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                _log($"更新の検証: SHA256が一致しない（期待={expected}, 実際={actual}）");
                throw new InvalidOperationException("落としたファイルが壊れています（ハッシュが合いません）。");
            }

            _log("更新の検証: SHA256が一致した");
        }
        else
        {
            _log("更新の検証: リリースにハッシュが無いので確かめていません（通信は HTTPS で守られています）");
        }

        return path;
    }

    private void LogDownloadFailure(Exception ex, long received, UpdateInfo info) =>
        _log($"更新のダウンロード: 失敗。{UpdateDiagnostics.Summarize(ex)}" +
             $"（受信済み {received}バイト / 想定 {info.SizeBytes}バイト）");

    /// <summary>先頭の2バイトが <c>MZ</c>（Windows の実行ファイル）か。</summary>
    private static async Task<bool> StartsWithExecutableHeaderAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        var head = new byte[2];
        var read = await stream.ReadAsync(head, cancellationToken).ConfigureAwait(false);

        return UpdateDiagnostics.LooksLikeExecutable(head.AsSpan(0, read));
    }

    /// <summary>
    /// いまの exe があるフォルダに書けるか。
    /// <para>Program Files などに置かれていると書けないので、自分では入れ替えられない。</para>
    /// </summary>
    public static bool CanWriteToInstallDirectory(out string directory)
    {
        directory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
        if (directory.Length == 0) return false;

        try
        {
            var probe = Path.Combine(directory, $".kado-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 落としたものと入れ替えて、新しいほうを起動する。
    /// <para>うまくいったら（<see cref="SwapResult.Succeeded"/>）、呼んだ側はすぐアプリを終わらせること。</para>
    /// <para>
    /// 70MB のコピーなので<b>別スレッドで行う</b>。画面のスレッドで行うと、その間
    /// 「入れ替えています…」が描かれないまま固まって見える。例外は投げず、結果の種類と文言で返す。
    /// 起動に失敗したときも元の版へ巻き戻す（<see cref="ExecutableSwap"/>）。
    /// </para>
    /// </summary>
    /// <param name="downloadedExe">入れ替える新しい exe。</param>
    /// <param name="keepHidden">新しいほうに、窓を隠したまま起動するよう伝えるか（トレイに入っていたとき）。</param>
    internal Task<SwapResult> ApplyAsync(string downloadedExe, bool keepHidden = false) =>
        Task.Run(() => Apply(downloadedExe, keepHidden));

    /// <summary>
    /// <see cref="ApplyAsync"/> の同期版。起動の最初（窓を出す前）の入れ替えで、画面のスレッドのまま使う。
    /// </summary>
    internal SwapResult Apply(string downloadedExe, bool keepHidden = false)
    {
        if (Environment.ProcessPath is not { Length: > 0 } current)
        {
            _log("更新の入れ替え: いまの exe の場所が分かりませんでした");

            return new SwapResult(
                SwapOutcome.FailedUnchanged, new InvalidOperationException("exe の場所が不明"), string.Empty, string.Empty);
        }

        return ExecutableSwap.Run(current, downloadedExe, exe => Launch(exe, keepHidden), _log);
    }

    /// <summary>入れ替えた exe を起動する。前のプロセスがまだ終わっていないので、待つよう伝える。</summary>
    private static void Launch(string exe, bool keepHidden)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = true };
        start.ArgumentList.Add(AfterUpdateArgument);
        if (keepHidden) start.ArgumentList.Add(KeepHiddenArgument);
        Process.Start(start);
    }

    /// <summary>
    /// 自動更新の置き場所に残った、使わないファイルを消す。起動時に呼ぶ。
    /// <para>
    /// 入れ替え待ちとして控えてあるファイル（<paramref name="keep"/>）以外を消す。入れ替えに失敗して残った
    /// ものや、落とす途中で終わったものが、積み上がらないようにする。
    /// </para>
    /// </summary>
    public void CleanupStaleStagedFiles(string? keep)
    {
        try
        {
            if (!Directory.Exists(StagedDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(StagedDirectory))
            {
                if (keep is not null &&
                    string.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても支障はない。次の起動でまた試す
        }
    }

    /// <summary>前回の入れ替えで残ったものを片付ける。起動時に呼ぶ。</summary>
    public void CleanupOldFiles()
    {
        if (Environment.ProcessPath is { Length: > 0 } current)
        {
            TryDelete(current + OldSuffix);
            TryDelete(current + ExecutableSwap.FailedSuffix);
        }

        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても支障はない。次の起動でまた試す
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 通信に使うクライアント。<b>タイムアウトは無限</b>にしてある（確認は要求ごとの上限、
    /// ダウンロードは読み取りが止まったときの上限を、呼ぶ側が掛ける）。
    /// </summary>
    private static HttpClient CreateClient(bool allowAutoRedirect)
    {
        // 作り方は共通の工場（KadoHttp）。認証付きプロキシ（407）へ、ログオン中のユーザーの
        // 資格情報を渡す設定もそこにある
        var http = KadoHttp.CreateClient(Timeout.InfiniteTimeSpan, allowAutoRedirect);

        // GitHub の API は名乗らないと断ることがある
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kado", CurrentVersion.ToString()));

        // Accept は要求ごとに付ける。以前はここで JSON を既定にしていたので、実行ファイルを
        // 取りに行く要求にまで「JSON をください」と言っていた。中身とヘッダの不一致を見る
        // 経路（会社のプロキシなど）に弾かれる余地を残す理由は無い

        return http;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
