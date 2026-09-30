using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    private const string OldSuffix = ".old";

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

    /// <summary>落としたものを置く場所。</summary>
    private static string TempDir => Path.Combine(Path.GetTempPath(), "Kado", "update");

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
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!ReleaseFeed.IsAllowedDownloadUrl(info.DownloadUrl))
        {
            _log($"更新のダウンロード: 取得先が許されていない場所のため中止 {UpdateDiagnostics.SafeUrl(info.DownloadUrl)}");
            throw new InvalidOperationException("更新の取得先が許されていない場所です。");
        }

        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"Kado-{info.TagName}.exe");

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
    /// <para>うまくいったら、呼んだ側はすぐアプリを終わらせること。</para>
    /// </summary>
    /// <returns>入れ替えられたら true。失敗したら元に戻して false。</returns>
    public bool Apply(string downloadedExe)
    {
        if (Environment.ProcessPath is not { Length: > 0 } current)
        {
            _log("更新の入れ替え: いまの exe の場所が分かりませんでした");
            return false;
        }

        var backup = current + OldSuffix;
        var renamed = false;

        try
        {
            // 前回の入れ替えで残ったものを先に片付ける
            TryDelete(backup);

            // 実行中の exe は上書きできないが、名前は変えられる
            File.Move(current, backup);
            renamed = true;

            File.Copy(downloadedExe, current, overwrite: true);

            // 前のプロセスがまだ終わっていないので、新しいほうには待つよう伝える
            var start = new ProcessStartInfo(current) { UseShellExecute = true };
            start.ArgumentList.Add(AfterUpdateArgument);
            Process.Start(start);

            TryDelete(downloadedExe);

            _log("更新の入れ替え: 新しい版に入れ替えて起動しました");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log($"更新の入れ替え: 失敗。{UpdateDiagnostics.Summarize(ex)}");

            // 途中で転んだなら、名前を戻して元の版で動けるようにする
            if (renamed)
            {
                try
                {
                    TryDelete(current);
                    File.Move(backup, current);
                    _log("更新の入れ替え: 元の版に戻しました");
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    _log($"更新の入れ替え: 元に戻すのにも失敗。{UpdateDiagnostics.Summarize(rollback)}。" +
                                $"{backup} を {current} に手で戻してください");
                }
            }

            return false;
        }
    }

    /// <summary>前回の入れ替えで残ったものを片付ける。起動時に呼ぶ。</summary>
    public void CleanupOldFiles()
    {
        if (Environment.ProcessPath is { Length: > 0 } current) TryDelete(current + OldSuffix);

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
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub の API は名乗らないと断ることがある
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kado", CurrentVersion.ToString()));

        // Accept は要求ごとに付ける。以前はここで JSON を既定にしていたので、実行ファイルを
        // 取りに行く要求にまで「JSON をください」と言っていた。中身とヘッダの不一致を見る
        // 経路（会社のプロキシなど）に弾かれる余地を残す理由は無い

        return http;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
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
