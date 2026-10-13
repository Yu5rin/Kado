using System.IO;
using Microsoft.Win32;
using Kado.Presentation.Settings;

namespace Kado.App.Settings;

/// <summary>
/// Windows にログオンしたときの自動起動。
/// <para>
/// <c>HKCU</c> の Run キーに実行ファイルのパスを書く。ユーザー単位なので管理者権限は要らない。
/// </para>
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kado";

    private readonly string? _path;

    public StartupRegistration(string? executablePath = null) =>
        // 単一ファイルとして配る形なので、ProcessPath が実行ファイルそのものを指す
        _path = executablePath ?? Environment.ProcessPath;

    /// <summary>実行ファイルの場所が分からなければ、登録のしようがない。</summary>
    public bool IsSupported => OperatingSystem.IsWindows() && _path is { Length: > 0 };

    public bool IsEnabled
    {
        get
        {
            if (!IsSupported) return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                    && value.Contains(_path!, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 自動起動が有効で、登録された exe の場所がいまの exe と違うときだけ、いまの場所に書き直す。
    /// <para>
    /// exe と data をまとめて別の場所へコピーしたあと、元の場所を指したままだと、ログオンしても起動しない。
    /// 自動起動を使っていなければ（登録が無ければ）何もしない。
    /// </para>
    /// </summary>
    /// <returns>shell.log に残す1行。何もしなかったときは null。</returns>
    public string? RepairIfMoved()
    {
        if (!IsSupported) return null;

        try
        {
            string? registered;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                registered = key?.GetValue(ValueName) as string;
            }

            if (!StartupPathCheck.NeedsRewrite(registered, _path!)) return null;

            return SetEnabled(true)
                ? $"自動起動: 登録の場所が違っていたので、いまの場所に書き直した（{registered} → {_path}）"
                : $"自動起動: 登録の場所が違っていたが、書き直せなかった（{registered}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"自動起動: 登録を確かめられなかった（{ex.GetType().Name}）";
        }
    }

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                // 空白を含むパスでも壊れないよう引用符でくくる
                key.SetValue(ValueName, $"\"{_path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
