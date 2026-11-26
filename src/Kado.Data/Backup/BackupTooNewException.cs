namespace Kado.Data.Backup;

/// <summary>
/// 復元しようとしたバックアップが、このアプリより新しい版の Kado で取ったもの。
/// <para>
/// 復元しても古い版では開けないので、いまのデータに触れる前に断る。
/// 復元まわりの呼び出し側が <see cref="IOException"/> を受けて画面に出せるよう、その派生にしてある。
/// </para>
/// </summary>
public sealed class BackupTooNewException(string path, int found, int latest) : IOException(
    $"新しい版の Kado で取ったバックアップです（スキーマ版 {found}、このアプリは {latest} まで）。"
    + $"Kado を新しい版に更新してから復元してください: {path}")
{
    /// <summary>バックアップのスキーマ版。</summary>
    public int Found { get; } = found;

    /// <summary>このアプリが知っている最新の版。</summary>
    public int Latest { get; } = latest;
}
