using System.Net;
using System.Net.Http;
using System.Text;
using Kado.Core.Net;
using Kado.Presentation.Net;

namespace Kado.Presentation.Update;

/// <summary>「通信を確かめる」の1か所ぶんの結果。</summary>
/// <param name="Name">画面に出す名前。</param>
/// <param name="Ok">届いたか。</param>
/// <param name="Detail">成功なら受け取った量、失敗なら状態コードや理由。</param>
public sealed record ConnectionProbeStep(string Name, bool Ok, string Detail);

/// <summary>「通信を確かめる」の結果。</summary>
public sealed record ConnectionProbeReport(IReadOnlyList<ConnectionProbeStep> Steps)
{
    /// <summary>すべて届いたか。</summary>
    public bool AllOk => Steps.All(s => s.Ok);

    /// <summary>画面に出す文。</summary>
    public string ToDisplayText()
    {
        var sb = new StringBuilder();

        foreach (var step in Steps)
        {
            sb.Append("・").Append(step.Name).Append("：").Append(step.Ok ? "成功" : "失敗")
                .Append("（").Append(step.Detail).AppendLine("）");
        }

        sb.AppendLine();

        if (AllOk)
        {
            sb.Append("すべて届きました。更新できるはずです。");
        }
        else if (Steps.Count == 3 && Steps[0].Ok && !Steps[1].Ok && Steps[2].Ok)
        {
            // API だけが断られる。Atom と配布物の置き場に届くなら、更新は通る
            sb.Append("リリース情報だけが届いていません。Kado は更新の一覧と配布ファイルだけでも更新できます" +
                      "（変更点の本文と、ファイルの照合を省きます）。");
        }
        else
        {
            sb.Append("届かない場所があります。詳しい記録は、データの保存先の shell.log にあります。");
        }

        return sb.ToString();
    }
}

/// <summary>
/// 更新の確認と、通信の試験。通信の中身はここに集め、画面や実行ファイルの入れ替えとは切り離す。
/// <para>
/// <b>確認は Atom フィードを先に見る。</b>GitHub の API には未認証で 1 時間 60 回の上限があり、
/// 同じ出口の IP で共有される。会社の回線では毎回 403 で断られて、一度も更新できなかった
/// （同じ作者の Pane の実機ログ）。Atom は別枠なので、最新のタグだけをこちらで見る。
/// Atom で「最新版」と分かれば API には行かない。新しい版があるときだけ API へ詳細
/// （変更点の本文・添付の大きさ・SHA256）を取りに行く。
/// </para>
/// <para>
/// <b>API が上限（403）や障害で失敗しても、更新できるようにする。</b>配布物の名前は
/// 決まっているので（<see cref="UpdateLinks.BuildAssetFileName"/>）、URL を組み立てて落とす。
/// その場合は SHA256 が分からないので、照合は省く。行き先は組み立てたあとも
/// <see cref="ReleaseFeed.IsAllowedDownloadUrl"/> を通る。
/// </para>
/// <para>
/// 失敗の理由は 1 行ずつ <c>log</c> へ渡す（App 側では shell.log に書く）。
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>確認全体の上限。Atom と API の2回ぶんをまとめて打ち切る。</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Atom 1回ぶんの上限。数十 KB の軽い応答なので、早めに見切って API に残りを回す。</summary>
    public static readonly TimeSpan AtomTimeout = TimeSpan.FromSeconds(8);

    /// <summary>API 1回ぶんの上限。</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>「通信を確かめる」の、配布ファイルの置き場に対する上限。</summary>
    public static readonly TimeSpan ProbeFileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>API の応答の大きさの上限。変更点の本文を含めても数十 KB。</summary>
    public const int MaxApiBytes = 2 * 1024 * 1024;

    /// <summary>「通信を確かめる」で配布ファイルから受け取ってみる量。中身が流れてくるかを見るだけ。</summary>
    public const int ProbeFileBytes = 256 * 1024;

    /// <summary>「通信を確かめる」で API から受け取ってみる量。</summary>
    private const int ProbeApiBytes = 64 * 1024;

    private readonly string _apiUrl;
    private readonly Version _current;
    private readonly Func<bool, HttpClient> _createClient;
    private readonly Action<string> _log;
    private readonly IWebProxy? _proxy;

    private int _proxyLogged;

    /// <param name="apiUrl">API の問い合わせ先（<c>.../releases/latest</c>）。Atom の URL はここから組み立てる。</param>
    /// <param name="currentVersion">いま動いている版。</param>
    /// <param name="createClient">
    /// 通信に使う <see cref="HttpClient"/> を作る。引数は自動のリダイレクトを許すか。
    /// <b>タイムアウトは無限にしておく</b>（こちらが要求ごとに上限を掛ける）。
    /// </param>
    /// <param name="log">1行ずつの記録。例外は投げないこと。</param>
    /// <param name="proxy">経路の記録に使う。null なら <see cref="HttpClient.DefaultProxy"/>。</param>
    public UpdateChecker(
        string apiUrl, Version currentVersion, Func<bool, HttpClient> createClient,
        Action<string>? log = null, IWebProxy? proxy = null)
    {
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        _current = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
        _log = log ?? (_ => { });
        _proxy = proxy;
    }

    /// <summary>
    /// 通信がどの経路を通るのかを記録する（プロセスで1回だけ）。
    /// <para>
    /// 会社の回線は、Windows の設定や PAC でプロキシ経由になっていることが多い。
    /// 通っているかどうかが分かるだけで、切り分けがかなり進む。
    /// </para>
    /// </summary>
    public void LogNetworkEnvironmentOnce(string url)
    {
        if (Interlocked.Exchange(ref _proxyLogged, 1) != 0) return;

        try
        {
            // 認証付きプロキシへは、ログオン中のユーザーの資格情報を渡す設定で通信している（KadoHttp）
            _log("更新の通信: " + UpdateDiagnostics.DescribeProxy(
                _proxy ?? HttpClient.DefaultProxy, new Uri(url), KadoHttp.UsesDefaultProxyCredentials));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException
                                       or InvalidOperationException)
        {
            _log($"更新の通信: 経路を調べられなかった（{ex.GetType().Name}）");
        }
    }

    // ------------------------------------------------------------------
    // 確認
    // ------------------------------------------------------------------

    /// <summary>
    /// 新しい版があるか見る。
    /// <para>
    /// 通信できなくても例外は投げず、<see cref="UpdateCheckStatus.Failed"/> を返す。
    /// 更新を確かめられないことは、アプリが使えない理由にはならない。
    /// </para>
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var atomUrl = UpdateLinks.TryBuildAtomUrl(_apiUrl);
        string? knownTag = null;

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalTimeout);

        try
        {
            _log($"更新の確認: 開始（現在 {_current}, 先に見る所={(atomUrl is null ? "API" : "Atom")}）");
            LogNetworkEnvironmentOnce(atomUrl ?? _apiUrl);

            using var http = _createClient(true);

            // まず Atom で最新のタグだけを見る（API の回数上限を受けない）
            if (atomUrl is not null &&
                await TryReadLatestTagAsync(http, atomUrl, total.Token).ConfigureAwait(false) is { } tag)
            {
                UpdateLinks.TryParseTag(tag, out var latest);

                if (latest <= _current)
                {
                    _log($"更新の確認: 最新版だった（現在 {_current}, 配布元 {tag}, 問い合わせ先=Atom）");
                    return UpdateCheckResult.UpToDate();
                }

                _log($"更新の確認: 新しい版がある（現在 {_current}, 配布元 {tag}, 問い合わせ先=Atom）。詳細を API へ問い合わせる");
                knownTag = tag;
            }

            // 新しい版があるとき（または Atom を読めなかったとき）だけ API へ
            var info = await ReadReleaseAsync(http, total.Token).ConfigureAwait(false);

            if (!ReleaseFeed.IsNewerThan(info, _current))
            {
                _log($"更新の確認: 最新版だった（現在 {_current}, 配布元 {info.TagName}, 問い合わせ先=API）");
                return UpdateCheckResult.UpToDate();
            }

            _log($"更新の確認: 新しい版がある（現在 {_current}, 配布元 {info.Version}, " +
                 $"サイズ={info.SizeBytes}バイト, SHA256={(info.Sha256 is null ? "(提供なし)" : "あり")}）");

            return UpdateCheckResult.Available(info);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 呼び出し側が止めたときは、ここへ来ない（そのまま伝える）
            _log($"更新の確認: 失敗。{UpdateDiagnostics.Summarize(ex)}");

            if (knownTag is not null && BuildInfoWithoutDetails(atomUrl, knownTag, ex) is { } fallback)
            {
                return UpdateCheckResult.Available(fallback);
            }

            return UpdateCheckResult.Failed(
                UpdateFailure.From(ex, UpdateLinks.BuildLatestReleasePageUrl(atomUrl)));
        }
    }

    /// <summary>
    /// 「新しい版があることは分かったが、詳細は取れなかった」ときの結果を作る。
    /// <para>
    /// API が上限（403）や障害で使えなくても、配布物の URL は規則から組み立てられる。
    /// SHA256 と変更点の本文は分からないので、照合を省き、窓には変更点を読めなかった旨を出す
    /// （<see cref="UpdateInfo.DetailsUnavailable"/>）。組み立てた URL も、許可された行き先か
    /// をここで確かめる。
    /// </para>
    /// </summary>
    private UpdateInfo? BuildInfoWithoutDetails(string? atomUrl, string tag, Exception cause)
    {
        var url = UpdateLinks.TryBuildDownloadUrl(atomUrl, tag);

        if (url is null || !ReleaseFeed.IsAllowedDownloadUrl(url) || !UpdateLinks.TryParseTag(tag, out var version))
        {
            _log($"更新の確認: 新しい版（{tag}）はあるが、取得先を組み立てられなかった。手で更新してもらう");
            return null;
        }

        _log($"更新の確認: 新しい版（{tag}）はあるが詳細を取れなかった（{UpdateFailure.Classify(cause)}）。" +
             $"取得先を組み立てて続行する（SHA256の照合は省く）: {UpdateDiagnostics.SafeUrl(url)}");

        return new UpdateInfo(
            version, tag, url, SizeBytes: 0, Sha256: null,
            ReleaseUrl: UpdateLinks.BuildReleasePageUrl(atomUrl, tag),
            ReleaseNotes: string.Empty,
            DetailsUnavailable: true);
    }

    /// <summary>
    /// Atom から最新のタグを読む。読めなければ null（失敗として扱わず、API へ進む）。
    /// </summary>
    private async Task<string?> TryReadLatestTagAsync(HttpClient http, string atomUrl, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(AtomTimeout);

        try
        {
            _log($"更新の確認: Atom に問い合わせる {UpdateDiagnostics.SafeUrl(atomUrl)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, atomUrl);
            request.Headers.Accept.Add(new("application/atom+xml"));

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            LogResponse("Atom", atomUrl, response);
            UpdateHttp.EnsureSuccess(response);

            // プロキシのエラーページが 200 で返ることがある。読む前に弾く
            if (UpdateDiagnostics.IsHtml(response.Content.Headers.ContentType?.ToString()))
            {
                throw new UnexpectedContentException(response.Content.Headers.ContentType!.ToString());
            }

            var bytes = await UpdateHttp.ReadLimitedAsync(response.Content, AtomFeed.MaxBytes, cts.Token)
                .ConfigureAwait(false);

            var tag = AtomFeed.ReadLatestTag(Encoding.UTF8.GetString(bytes));

            if (tag is null)
            {
                _log($"更新の確認: Atom から版のタグを読めなかった（{bytes.Length}バイト受信）。API へ問い合わせる");
            }

            return tag;
        }
        catch (Exception ex) when (!outer.IsCancellationRequested)
        {
            // Atom が使えなくても、API で確かめられる。全体の時間切れのときは、ここで握らず外へ出す
            _log($"更新の確認: Atom を読めなかった。API へ問い合わせる。{UpdateDiagnostics.Summarize(ex)}");
            return null;
        }
    }

    /// <summary>API からリリースを読む。失敗は例外にする。</summary>
    private async Task<UpdateInfo> ReadReleaseAsync(HttpClient http, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(ApiTimeout);

        _log($"更新の確認: API に問い合わせる {UpdateDiagnostics.SafeUrl(_apiUrl)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
        request.Headers.Accept.Add(new("application/vnd.github+json"));

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

        LogResponse("API", _apiUrl, response);
        UpdateHttp.EnsureSuccess(response);

        if (UpdateDiagnostics.IsHtml(response.Content.Headers.ContentType?.ToString()))
        {
            throw new UnexpectedContentException(response.Content.Headers.ContentType!.ToString());
        }

        var bytes = await UpdateHttp.ReadLimitedAsync(response.Content, MaxApiBytes, cts.Token)
            .ConfigureAwait(false);

        return ReleaseFeed.Parse(Encoding.UTF8.GetString(bytes))
               ?? throw new InvalidDataException("リリースの中身を読めませんでした。");
    }

    private void LogResponse(string label, string requestedUrl, HttpResponseMessage response)
    {
        _log($"更新の確認: {label} {UpdateDiagnostics.DescribeResponse(response)}");

        if (UpdateDiagnostics.DescribeRedirect(requestedUrl, response) is { } redirect)
        {
            _log($"更新の確認: {label} {redirect}");
        }
    }

    // ------------------------------------------------------------------
    // 通信の試験
    // ------------------------------------------------------------------

    /// <summary>
    /// Atom・API・配布ファイルの置き場に実際に接続して、何が返るかを確かめる。
    /// <para>
    /// <b>なぜ「更新の確認」と別に要るか。</b>ログを出せるようにした版を会社の PC へ入れると、
    /// その時点で最新版になり、更新するものが無いので、ダウンロードを試す手段そのものが
    /// 無くなってしまう（Pane で実際に起きた）。最新版のままでも通信だけを試せる入口が要る。
    /// </para>
    /// <para>
    /// 受け取るのは先頭の一部だけで、<b>ファイルは保存しない</b>（更新もしない）。
    /// 結果は画面用の <see cref="ConnectionProbeReport"/> に返し、詳細は 1 行ずつ記録に残す。
    /// </para>
    /// </summary>
    public async Task<ConnectionProbeReport> ProbeConnectionAsync(CancellationToken cancellationToken = default)
    {
        var atomUrl = UpdateLinks.TryBuildAtomUrl(_apiUrl);

        _log("更新の通信確認: 開始");
        LogNetworkEnvironmentOnce(atomUrl ?? _apiUrl);

        // リダイレクトは自分で辿り、転送先も許可された場所かを確かめる（配布物の確認と同じ経路）
        using var http = _createClient(false);

        var steps = new List<ConnectionProbeStep>();
        string? tag = null;

        // 1. Atom
        if (atomUrl is null)
        {
            steps.Add(new("更新の一覧（Atom）", false, "GitHub 以外の問い合わせ先のため対象外"));
        }
        else
        {
            var (atomStep, body) = await ProbeAsync(
                http, "更新の一覧（Atom）", atomUrl, "application/atom+xml",
                AtomTimeout, AtomFeed.MaxBytes, cancellationToken).ConfigureAwait(false);

            if (atomStep.Ok)
            {
                tag = AtomFeed.ReadLatestTag(Encoding.UTF8.GetString(body!));

                if (tag is null)
                {
                    _log("更新の通信確認: Atom は届いたが、版のタグを読めなかった");
                    atomStep = new("更新の一覧（Atom）", false,
                        UpdateFailure.ShortReason(NetworkFailureKind.UnreadableResponse));
                }
            }

            steps.Add(atomStep);
        }

        // 2. API
        var (apiStep, _) = await ProbeAsync(
            http, "リリース情報（API）", _apiUrl, "application/vnd.github+json",
            ApiTimeout, ProbeApiBytes, cancellationToken).ConfigureAwait(false);
        steps.Add(apiStep);

        // 3. 配布ファイル。API を使わずに組み立てた URL（実際に API が使えないときに通る道）
        //    最新のタグが分からなければ、いまの版のファイルで確かめる
        var fileTag = tag ?? $"v{_current.Major}.{_current.Minor}.{_current.Build}";
        var fileUrl = UpdateLinks.TryBuildDownloadUrl(atomUrl, fileTag);

        if (fileUrl is null)
        {
            steps.Add(new("配布ファイルの置き場", false, "取得先を組み立てられない"));
        }
        else
        {
            var (fileStep, head) = await ProbeAsync(
                http, "配布ファイルの置き場", fileUrl, "application/octet-stream",
                ProbeFileTimeout, ProbeFileBytes, cancellationToken).ConfigureAwait(false);

            // 200 でも、中身が実行ファイルの形でなければ、途中で差し替えられている
            if (fileStep.Ok && !UpdateDiagnostics.LooksLikeExecutable(head!))
            {
                _log("更新の通信確認: 配布ファイルの先頭が実行ファイルの形ではない");
                fileStep = new("配布ファイルの置き場", false, "実行ファイルではないものが返った");
            }

            steps.Add(fileStep);
        }

        var report = new ConnectionProbeReport(steps);

        _log("更新の通信確認: 終了。" +
             string.Join(" / ", steps.Select(s => $"{s.Name}={(s.Ok ? "成功" : $"失敗（{s.Detail}）")}")));

        return report;
    }

    /// <summary>
    /// 1か所へ接続し、先頭を <paramref name="maxBytes"/> だけ受け取って切る。保存はしない。
    /// </summary>
    private async Task<(ConnectionProbeStep Step, byte[]? Body)> ProbeAsync(
        HttpClient http, string name, string url, string accept,
        TimeSpan timeout, int maxBytes, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            _log($"更新の通信確認: {name}: 接続する {UpdateDiagnostics.SafeUrl(url)}");

            using var response = await UpdateHttp
                .FollowAllowedRedirectsAsync(http, url, accept, cts.Token, m => _log($"更新の通信確認: {name}: {m}"))
                .ConfigureAwait(false);

            _log($"更新の通信確認: {name}: {UpdateDiagnostics.DescribeResponse(response)}");

            if (UpdateDiagnostics.DescribeRedirect(url, response) is { } redirect)
            {
                _log($"更新の通信確認: {name}: {redirect}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var kind = UpdateFailure.ClassifyStatus(response.StatusCode) ?? NetworkFailureKind.Other;

                return (new(name, false, $"HTTP {(int)response.StatusCode} {response.StatusCode}、" +
                                         UpdateFailure.ShortReason(kind)), null);
            }

            if (UpdateDiagnostics.IsHtml(response.Content.Headers.ContentType?.ToString()))
            {
                return (new(name, false, UpdateFailure.ShortReason(NetworkFailureKind.WebPageInsteadOfFile)), null);
            }

            // 先頭だけ受け取って切る。ここまで来れば、繋がって中身が流れてくることは分かる
            var received = await ReadHeadAsync(response.Content, maxBytes, cts.Token).ConfigureAwait(false);

            _log($"更新の通信確認: {name}: {received.Length}バイトを受け取れた（先頭のみ。ファイルは保存していない）");

            if (received.Length == 0)
            {
                return (new(name, false, "接続はできたが、中身を受け取れなかった"), null);
            }

            return (new(name, true, $"{FormatBytes(received.Length)}を受け取りました"), received);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log($"更新の通信確認: {name}: 失敗。{UpdateDiagnostics.Summarize(ex)}");

            var kind = UpdateFailure.Classify(ex);

            // 状態コードは、種類とは別に見せる（403/407 などがそのまま手掛かりになる）
            var status = UpdateFailure.Flatten(ex).OfType<HttpRequestException>()
                .FirstOrDefault(h => h.StatusCode is not null)?.StatusCode;

            return (new(name, false, status is { } code
                ? $"HTTP {(int)code} {code}、{UpdateFailure.ShortReason(kind)}"
                : UpdateFailure.ShortReason(kind)), null);
        }
    }

    /// <summary>先頭を <paramref name="maxBytes"/> まで読む。それ以上は読まずに返す。</summary>
    private static async Task<byte[]> ReadHeadAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[16 * 1024];

        while (buffer.Length < maxBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 ? $"{bytes / 1024}KB" : $"{bytes}バイト";
}
