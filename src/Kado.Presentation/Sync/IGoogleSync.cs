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

    /// <summary>
    /// 接続を切る。Google 側の許可も取り消す。
    /// <para>
    /// 取り消しの通信が失敗しても、こちらの控え（トークン）と同期の状態は必ず消す。
    /// 取り消しが Google に届いたかは戻り値で返す（false なら、Google アカウントの設定から
    /// 手で外してもらう）。控えを消せなかったときだけ例外になる。
    /// </para>
    /// </summary>
    /// <returns>Google 側の取り消しが届いたか（取り消すものが無かったときも true）。</returns>
    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>一度だけ同期する。すでに走っていれば null。</summary>
    Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 直前の同期が中止・失敗で途中から抜けたとき、そこまでに手元へ書き込んだかもしれないか。
    /// <para>
    /// 同期は別の接続で書くので、画面は書いたことを自分では知らない。true なら画面を読み直す。
    /// 既定は <b>true</b>（分からないときは読み直す側に倒す）。実物は、最初の通信の前に
    /// 転んだだけのとき（回線が無いなど）を false で返し、むだな読み直しを省く。
    /// </para>
    /// </summary>
    bool MayHaveWrittenBeforeInterruption => true;

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
