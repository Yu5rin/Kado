using System.Net.Http;
using SlideinaCalendar.Core.Import;

namespace SlideinaCalendar.Presentation;

/// <summary>
/// 配信元から実働日データ（feed.json）を取りに行く。
/// <para>
/// <b>https のみ。</b>取り込んだ内容は実働日の判定そのものになるので、途中で
/// 書き換えられたものを読むわけにいかない。
/// </para>
/// <para>
/// 落としたものは JSON として読むだけで、実行はしない。大きすぎるものは途中で切る。
/// </para>
/// </summary>
public sealed class WorkdayFeedClient(HttpClient? client = null)
{
    /// <summary>受け取る上限。実働日データは10年ぶんでも数百KBに収まる。</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _client = client ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>取りに行って読む。</summary>
    /// <param name="url">配信元。https であること。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    public async Task<ImportResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Settings.AppSettings.IsUsableFeedUrl(url))
        {
            throw new InvalidOperationException("配信元は https:// で始まる URL にしてください。");
        }

        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
        {
            throw new InvalidDataException($"配信元のファイルが大きすぎます（{length / 1024}KB）。");
        }

        var json = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        return WorkdayFeed.Read(json);
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
