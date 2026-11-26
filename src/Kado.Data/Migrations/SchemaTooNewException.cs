namespace Kado.Data.Migrations;

/// <summary>
/// データベースのスキーマ版が、このアプリの知っている最新版より新しい。
/// <para>
/// 起動時の失敗の見分け（<see cref="DatabaseOpenFailure"/>）で「新しい版のデータ」と判断するのは、
/// この型だけにする。ほかの <see cref="InvalidOperationException"/> まで「新しい版」と案内しない。
/// </para>
/// </summary>
public sealed class SchemaTooNewException(int found, int latest) : InvalidOperationException(
    $"データベースのスキーマ版 {found} は、このアプリが知っている最新版 {latest} より新しいため開けません。"
    + "新しいバージョンのアプリで開いてください。")
{
    /// <summary>データベースの版。</summary>
    public int Found { get; } = found;

    /// <summary>このアプリが知っている最新の版。</summary>
    public int Latest { get; } = latest;
}
