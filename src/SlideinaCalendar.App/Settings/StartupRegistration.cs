using System.IO;
using Microsoft.Win32;
using SlideinaCalendar.Presentation.Settings;

namespace SlideinaCalendar.App.Settings;

/// <summary>
/// Windows にログオンしたときの自動起動。
/// <para>
/// <c>HKCU</c> の Run キーに実行ファイルのパスを書く。ユーザー単位なので管理者権限は要らない。
/// </para>
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SlideinaCalendar";

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
