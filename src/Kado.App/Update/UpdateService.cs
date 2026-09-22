using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Kado.Presentation.Update;

namespace Kado.App.Update;

/// <summary>いま確かめている状態。押し直したときに「最新です」と誤って言わないための4状態。</summary>
public enum UpdateCheckStatus
{
    /// <summary>いま別の確認が進んでいて、始められなかった。</summary>
    AlreadyChecking,

    /// <summary>確かめられなかった（通信できない、応答を読めないなど）。</summary>
    Failed,

    /// <summary>最新版を使っている。</summary>
    UpToDate,

    /// <summary>新しい版がある。</summary>
    UpdateAvailable,
}

/// <summary>
/// <see cref="UpdateService.CheckAsync"/> の結果。
/// <para>
/// 以前は「新しい版があれば <see cref="UpdateInfo"/>、無ければ <c>null</c>」だけを返していたが、
/// これだと「確認中で始められなかった」ときも <c>null</c> になり、呼び出し側が「最新です」と
/// 誤って伝えてしまっていた。状態を4つに分けて区別できるようにする。
/// </para>
/// </summary>
/// <param name="Status">いまの状態。</param>
/// <param name="Info"><see cref="UpdateCheckStatus.UpdateAvailable"/> のときだけ入る。</param>
public readonly record struct UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info = null)
{
    public static UpdateCheckResult AlreadyChecking() => new(UpdateCheckStatus.AlreadyChecking);

    public static UpdateCheckResult Failed() => new(UpdateCheckStatus.Failed);

    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);

    public static UpdateCheckResult Available(UpdateInfo info) => new(UpdateCheckStatus.UpdateAvailable, info);
}

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
public sealed class UpdateService(string apiUrl, Action<string>? log = null)
{
    /// <summary>入れ替え直後の起動だと新しいほうへ伝える合図。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>
    /// 入れ替え直後は、前のプロセスが終わるのを待ってから二重起動を判定する。
    /// <para>待たないと「すでに起動しています」で即座に終わってしまう。</para>
    /// </summary>
    public static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(30);

    private const string OldSuffix = ".old";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 落とすときの、読み取りが止まったとみなす長さ。
    /// <para>
    /// 全体の時間ではなく<b>アイドルタイムアウト</b>にしてある。70MB を遅い回線
    /// （120KB/s 未満）で落とすと数分かかり、全体に上限を掛けると毎回そこで切れて
    /// しまう。1バイトでも来ていれば、そのたびにここから数え直す。
    /// </para>
    /// </summary>
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>リダイレクトを自分で辿るときの上限。</summary>
    private const int MaxRedirects = 5;

    private int _checking;

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
            log?.Invoke("すでに確認中です");
            return UpdateCheckResult.AlreadyChecking();
        }

        try
        {
            using var http = CreateClient(CheckTimeout);

            using var response = await http.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (ReleaseFeed.Parse(json) is not { } info)
            {
                log?.Invoke("リリースの中身を読めませんでした");
                return UpdateCheckResult.Failed();
            }

            if (!ReleaseFeed.IsNewerThan(info, CurrentVersion))
            {
                log?.Invoke($"最新版を使っています（いま {CurrentVersion}）");
                return UpdateCheckResult.UpToDate();
            }

            log?.Invoke($"新しい版があります（{CurrentVersion} → {info.Version}）");
            return UpdateCheckResult.Available(info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log?.Invoke($"更新を確かめられませんでした: {ex.Message}");
            return UpdateCheckResult.Failed();
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>
    /// 落とす。
    /// <para>ハッシュが付いていれば確かめる。合わなければ捨てて例外にする。</para>
    /// <para>
    /// <b>リダイレクトは自分で辿る。</b><c>AllowAutoRedirect</c> の既定（true）だと
    /// 最初の URL しか行き先を確かめず、途中で許されない場所へ跳ばされてもそのまま
    /// 辿って落としてしまう。行き先が変わるたびに <see cref="ReleaseFeed.IsAllowedDownloadUrl"/>
    /// を通す。
    /// </para>
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!ReleaseFeed.IsAllowedDownloadUrl(info.DownloadUrl))
        {
            throw new InvalidOperationException("更新の取得先が許されていない場所です。");
        }

        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"Kado-{info.TagName}.exe");

        // 読み取りが止まったときだけ打ち切る。1バイトでも来るたびに、ここから数え直す
        using var idle = new CancellationTokenSource(DownloadIdleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        try
        {
            // AllowAutoRedirect は使わず、行き先を自分で確かめながら辿る
            using var http = CreateClient(Timeout.InfiniteTimeSpan, allowAutoRedirect: false);

            using var response = await FollowAllowedRedirectsAsync(http, info.DownloadUrl, linked.Token)
                .ConfigureAwait(false);

            // ヘッダを受け取れた。ここから改めて読み取りを見張り直す
            idle.CancelAfter(DownloadIdleTimeout);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.SizeBytes;

            await using var source = await response.Content
                .ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

            await using var target = File.Create(path);

            var buffer = new byte[81920];
            long done = 0;
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が止めたのではなく、読み取りが1分止まって諦めた
            TryDelete(path);
            throw new TimeoutException(
                $"更新の取得が{(int)DownloadIdleTimeout.TotalSeconds}秒間止まったので中止しました。");
        }
        catch
        {
            // 書きかけを残さない。次に読むと壊れたものを掴む
            TryDelete(path);
            throw;
        }

        if (info.Sha256 is { Length: > 0 } expected)
        {
            // 70MB のハッシュ計算。画面のスレッドで行うと固まる
            var actual = await Task.Run(() => ComputeSha256(path), cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                throw new InvalidOperationException("落としたファイルが壊れています（ハッシュが合いません）。");
            }

            log?.Invoke("落としたファイルのハッシュを確かめました");
        }
        else
        {
            log?.Invoke("リリースにハッシュが無いので確かめていません（通信は HTTPS で守られています）");
        }

        return path;
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
            log?.Invoke("いまの exe の場所が分かりませんでした");
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

            log?.Invoke("新しい版に入れ替えて起動しました");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"入れ替えに失敗しました: {ex.Message}");

            // 途中で転んだなら、名前を戻して元の版で動けるようにする
            if (renamed)
            {
                try
                {
                    TryDelete(current);
                    File.Move(backup, current);
                    log?.Invoke("元の版に戻しました");
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"元に戻すのにも失敗しました: {rollback.Message}。" +
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

    private static HttpClient CreateClient(TimeSpan timeout, bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var http = new HttpClient(handler) { Timeout = timeout };

        // GitHub の API は名乗らないと断ることがある
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Kado", CurrentVersion.ToString()));

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return http;
    }

    /// <summary>
    /// リダイレクトを自分で辿り、行き先が変わるたびに許可された場所かを確かめる。
    /// <para>
    /// GitHub の Releases はアセットの実体を <c>githubusercontent.com</c> 側へ
    /// リダイレクトすることが多い。<c>AllowAutoRedirect</c> の既定はリダイレクト先を
    /// 検査しないので、応答の途中で差し替えられても気づけない。
    /// </para>
    /// </summary>
    private static async Task<HttpResponseMessage> FollowAllowedRedirectsAsync(
        HttpClient http, string url, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ReleaseFeed.IsAllowedDownloadUrl(url))
            {
                throw new InvalidOperationException("更新の取得先が許されていない場所です。");
            }

            var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            // 相対な Location もあり得るので、いまの URL を基準に組み立てる
            url = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(url), location).ToString();

            response.Dispose();
        }

        throw new InvalidOperationException("更新の取得でリダイレクトが多すぎました。");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

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
