using System.Net.Http;
using Kado.Core.Import;
using Kado.Core.Net;
using Kado.Presentation.Net;

namespace Kado.Presentation;

/// <summary>
/// 配信元から実働日データ（feed.json）を取りに行く。
/// <para>
/// <b>https のみ。</b>取り込んだ内容は実働日の判定そのものになるので、途中で
/// 書き換えられたものを読むわけにいかない。
/// </para>
/// <para>
/// 落としたものは JSON として読むだけで、実行はしない。大きすぎるものは途中で切る。
/// </para>
/// <para>
/// 通信の作り方は共通の工場（<see cref="KadoHttp"/>）。認証付きプロキシ（407）へ、ログオン中の
/// ユーザーの資格情報を渡す。失敗は shell.log に1行ずつ残す（<see cref="NetworkLog"/>）。
/// </para>
/// <para>
/// <b>呼ぶときは <c>Task.Run</c> の中で。</b>最初の HTTP 要求は、経路（プロキシ）の自動検出で
/// 呼んだスレッドのまま数秒止まることがある。
/// </para>
/// </summary>
public sealed class WorkdayFeedClient(HttpClient? client = null, NetworkLog? log = null)
{
    /// <summary>shell.log の見出し。</summary>
    private const string LogArea = "実働日の配信";

    /// <summary>
    /// 記録の出し先。呼び出し側（取得の段取りを決める側）も、判断の1行を同じ場所へ残す。
    /// </summary>
    public NetworkLog Log { get; } = log ?? NetworkLog.None;

    /// <summary>受け取る上限。実働日データは10年ぶんでも数百KBに収まる。</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 最初の通信のときに作る。起動のたびに作っても、その日取りに行かなければ使わない。
    /// 渡されたものがあればそれを使う。
    /// </summary>
    private readonly Lazy<HttpClient> _client = new(() => client ?? KadoHttp.CreateClient(KadoHttp.DefaultTimeout));

    /// <summary>取りに行って読む。</summary>
    /// <param name="url">配信元。https であること。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    public async Task<ImportResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Settings.AppSettings.IsUsableFeedUrl(url))
        {
            throw new InvalidOperationException("配信元は https:// で始まる URL にしてください。");
        }

        try
        {
            Log.LogProxyOnce(LogArea, url);

            using var response = await _client.Value
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 407（プロキシの認証）・403・5xx の区別と、中継のエラーページ（text/html）の見分けのため、
                // 応答の要点を残す。本文は残さない
                Log.Write(LogArea, NetworkDiagnostics.DescribeResponse(response) +
                                   $"（{NetworkDiagnostics.SafeUrl(url)}）");

                response.EnsureSuccessStatusCode();
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                throw new InvalidDataException($"配信元のファイルが大きすぎます（{length / 1024}KB）。");
            }

            var json = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            return WorkdayFeed.Read(json);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            Log.Failure(LogArea, ex, NetworkFailure.Classify(ex), "取得");
            throw;
        }
    }

    /// <summary>長さを名乗らない相手もいるので、読みながら上限で切る。</summary>
    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var limited = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            if (limited.Length + read > MaxBytes)
            {
                throw new InvalidDataException("配信元のファイルが大きすぎます。");
            }

            limited.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(limited.ToArray());
    }
}
