using System.IO;

namespace Kado.App.Shell;

/// <summary>
/// DB を介さない終了印（<c>workarea.reserved</c>）のファイル操作。
/// <para>
/// 置き場所を引数で受けるだけの、Win32 にも WPF にも依存しない部品。
/// <see cref="WorkAreaGuard"/> が本番の置き場所を渡し、テストは一時フォルダを渡す。
/// </para>
/// </summary>
internal static class WorkAreaMarker
{
    /// <summary>印を置く／消す。書けなくても支障は無いので、入出力の失敗は黙って諦める。</summary>
    public static void Set(string path, bool reserved)
    {
        try
        {
            if (reserved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書けなくても、DB 版の印が生きていれば次の起動で拾える
        }
    }
}
