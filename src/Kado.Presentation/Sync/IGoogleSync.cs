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

    /// <summary>
    /// いまの接続に、添付をアップロードするための権限（<c>drive.file</c>）があるか。
    /// <para>繋いでいなければ false。API を呼ばずに済む場面（ボタンの出し分けなど）で使う。</para>
    /// </summary>
    bool HasDriveAttachmentScope { get; }

    /// <summary>
    /// 添付をアップロードするための権限が無ければ、追加で認可を受ける。ブラウザが開く。
    /// <para>
    /// すでに持っていれば何もせず true。利用者が同意画面で断れば false（いまの接続は
    /// 壊さない）。既定のスコープ一式には含めていないので、添付を初めて足そうとした
    /// ときにだけ呼ぶ。
    /// </para>
    /// </summary>
    Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ドライブ API を組み立てる。添付のアップロードにだけ使う。
    /// <para>
    /// 呼ぶ前に <see cref="HasDriveAttachmentScope"/> を確かめること。権限が無いまま
    /// 呼ぶと、Google 側に断られる（403）。
    /// </para>
    /// </summary>
    Kado.Google.Sync.GoogleDriveApi CreateDriveApi();
}
