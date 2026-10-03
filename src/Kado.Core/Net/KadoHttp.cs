using System.Net;
using System.Net.Http;

namespace Kado.Core.Net;

/// <summary>
/// Kado が作るすべての <see cref="HttpClient"/> の出どころ。
/// <para>
/// 更新の確認、Google（同期・接続・添付・切断）、実働日の配信の取得が、同じ設定の
/// ハンドラを使うように、作り方をここに1か所だけ置く。<b>他の場所で <c>new HttpClient()</c>
/// を書かない</b>（<c>tests/Kado.App.Tests</c> がソースを見張っている）。
/// </para>
/// <para>
/// <b>認証付きプロキシ（407）への対処。</b>会社の回線は、Windows の設定や PAC でプロキシ経由に
/// なっていて、ログオン中のユーザーの資格情報（NTLM/Kerberos）での認証を求めることが多い。
/// 何も渡さない <see cref="HttpClient"/> は、その 407 を受けて通信が通らない。
/// <see cref="SocketsHttpHandler.DefaultProxyCredentials"/> に
/// <see cref="CredentialCache.DefaultCredentials"/> を渡すと、<b>システムのプロキシに対してだけ</b>
/// ログオン中のユーザーの資格情報を使う。Google や GitHub など、宛先のサーバーへは渡らない。
/// </para>
/// </summary>
public static class KadoHttp
{
    /// <summary>API 1回ぶんの既定の待ち時間。カレンダー数だけ呼ぶので、既定の100秒のままだと長すぎる。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 接続を使い回す長さ。プロキシや DNS の切り替え（VPN の接続・切断）に、常駐したままでも追いつく。
    /// </summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>プロキシへ、ログオン中のユーザーの資格情報を渡す設定にしているか。記録に残す用。</summary>
    public const bool UsesDefaultProxyCredentials = true;

    /// <summary>
    /// ハンドラを作る。
    /// </summary>
    /// <param name="allowAutoRedirect">
    /// リダイレクトを自動で辿るか。実行ファイルを落とすときは false にして、行き先を自分で確かめながら辿る。
    /// </param>
    public static SocketsHttpHandler CreateHandler(bool allowAutoRedirect = true) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        PooledConnectionLifetime = PooledConnectionLifetime,
    };

    /// <summary>
    /// クライアントを作る。呼んだ側が <see cref="IDisposable.Dispose"/> する。
    /// </summary>
    /// <param name="timeout">
    /// 1回の要求ぶんの待ち時間。null なら <see cref="DefaultTimeout"/>。
    /// 要求ごとに自分で上限を掛けるときは <see cref="Timeout.InfiniteTimeSpan"/> を渡す。
    /// </param>
    /// <param name="allowAutoRedirect">リダイレクトを自動で辿るか。</param>
    public static HttpClient CreateClient(TimeSpan? timeout = null, bool allowAutoRedirect = true) =>
        new(CreateHandler(allowAutoRedirect)) { Timeout = timeout ?? DefaultTimeout };
}
