using Kado.Google.Sync;

namespace Kado.Presentation.Sync;

/// <summary>
/// Google との繋ぎと同期。
/// <para>
/// 画面はこれしか知らない。実物はブラウザを開いて認可を受け、API を叩く。
/// テストでは差し替えて、画面の振る舞いだけを試す。
/// </para>
/// </summary>
public interface IGoogleSync
{
    /// <summary>繋いであるか。</summary>
    bool IsConnected { get; }

    /// <summary>繋ぐ支度ができているか。クライアント設定を読み込んでいなければ false。</summary>
    bool CanConnect { get; }

    /// <summary>認可を受けて繋ぐ。ブラウザが開く。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>接続を切る。Google 側の許可も取り消す。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>一度だけ同期する。すでに走っていれば null。</summary>
    Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default);
}
