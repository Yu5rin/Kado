using System.IO;

namespace Kado.App.Settings;

/// <summary>
/// Windows ログオン時の自動起動に登録された exe の場所が、いまの exe と同じかを見る。
/// <para>
/// exe と data をまとめて別の場所・別の PC へコピーすると、登録は元の場所を指したままになる。
/// レジストリには触れない純粋な判断（<see cref="StartupRegistration"/> が使う）。
/// </para>
/// </summary>
internal static class StartupPathCheck
{
    /// <summary>登録された値（<c>"C:\Kado\Kado.exe"</c> のように引用符でくくった形）から、exe のパスを取り出す。</summary>
    internal static string? ExtractPath(string? registered)
    {
        if (string.IsNullOrWhiteSpace(registered)) return null;

        var value = registered.Trim();

        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        // 引用符なしで書かれていた場合は、全体をパスと見なす
        return value;
    }

    /// <summary>
    /// 書き直す必要があるか。登録があり（自動起動が有効）、その場所がいまの exe と違うときだけ true。
    /// 登録が無いなら（自動起動を使っていない）書き直さない。
    /// </summary>
    internal static bool NeedsRewrite(string? registered, string currentExe)
    {
        var path = ExtractPath(registered);
        if (path is null || string.IsNullOrWhiteSpace(currentExe)) return false;

        return !string.Equals(Normalize(path), Normalize(currentExe), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
