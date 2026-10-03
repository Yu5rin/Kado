using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Kado.Data;
using Kado.Google.OAuth;
using Kado.Google.Sync;
using Microsoft.Data.Sqlite;

namespace Kado.Presentation.Sync;

/// <summary>
/// 添付を上げる途中で起きた例外を、利用者に出す理由の文言に変える。
/// <para>
/// 添付の追加は画面の <c>async void</c> から呼ばれるので、ここで漏らした例外は
/// アプリごと終わらせる。通信・認可・ファイル・設定の保存のどこで転んでも、文言にして返す。
/// 型名は出さない（例外の <c>Message</c> は、こちらで書いた日本語のものだけ添える）。
/// </para>
/// </summary>
public static class AttachmentFailure
{
    /// <summary>利用者の取り消しか。これだけは失敗として扱わず、呼び出し側へそのまま返す。</summary>
    public static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>理由の文言を作る。</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // こちらで書いた文言（接続情報が無い・更新トークンが切れた、など）
            OAuthException oauth => $"Google との接続を確かめられませんでした: {oauth.Message}",

            GoogleApiException api => $"アップロードできませんでした: {api.Reason}",

            HttpRequestException => "通信できませんでした。オフラインの可能性があります。",

            // 呼び出し側の取り消しは IsCancellation が先に除いている。ここへ来るのは通信の時間切れ
            OperationCanceledException => "通信がタイムアウトしました。",

            // プロキシやキャプティブポータルが、HTML を 200 で返したとき
            JsonException =>
                "Google からの応答を読み取れませんでした。会社のネットワークが別のページを返している可能性があります。",

            // 応答に id が無い
            KeyNotFoundException or InvalidOperationException =>
                "Google からの応答にファイルの情報が含まれていませんでした。時間をおいてもう一度お試しください。",

            UnauthorizedAccessException =>
                "ファイルを読む権限がありません。別の場所にコピーして選び直してください。",

            FileNotFoundException or DirectoryNotFoundException =>
                "ファイルが見つかりませんでした。移動や削除をされていないか確かめてください。",

            IOException io => $"ファイルを読めませんでした: {io.Message}",

            // 認可の受け口（localhost）を開けない。Win32Exception の一種なので、先に置く
            HttpListenerException =>
                "認可を受け取る準備ができませんでした。セキュリティ ソフトなどが通信を止めていないか確かめてください。",

            // 認可のためにブラウザを起動できない
            Win32Exception =>
                "ブラウザを開けませんでした。既定のブラウザの設定を確かめてください。",

            // フォルダ ID の控え（設定）を書けなかった
            SqliteException sqlite => SqliteFailure.DescribeSaveFailure(sqlite),

            _ => "添付を上げられませんでした。時間をおいてもう一度お試しください。",
        };
    }
}
