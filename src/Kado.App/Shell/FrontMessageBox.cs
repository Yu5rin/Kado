using System.Runtime.InteropServices;

namespace Kado.App.Shell;

/// <summary>前に出すメッセージの、ボタンの組み合わせ。</summary>
internal enum FrontButtons
{
    Ok,
    YesNo,
    YesNoCancel,
    RetryCancel,
}

/// <summary>前に出すメッセージの、アイコン。</summary>
internal enum FrontIcon
{
    Information,
    Warning,
    Error,
}

/// <summary>前に出すメッセージの、押されたもの。</summary>
internal enum FrontResult
{
    Ok,
    Yes,
    No,
    Cancel,
    Retry,
}

/// <summary>
/// 親の窓を持たずに、必ず前に出るメッセージを出す。
/// <para>
/// 起動の最初（窓がまだ無い）や、ログオンの直後に出すメッセージは、親が無いと
/// 他の窓の裏に隠れ、利用者が気づかないまま起動が止まって見える。WPF の
/// <c>MessageBox</c> は最前面（<c>MB_TOPMOST</c>）の指定を受け付けない
/// （未知のオプションは例外になる）ので、Win32 の <c>MessageBox</c> を直に呼ぶ。
/// </para>
/// </summary>
internal static class FrontMessageBox
{
    // MessageBox の種別（https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-messageboxw）
    private const uint MB_OK = 0x0;
    private const uint MB_YESNOCANCEL = 0x3;
    private const uint MB_YESNO = 0x4;
    private const uint MB_RETRYCANCEL = 0x5;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_ICONINFORMATION = 0x40;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;

    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int IDRETRY = 4;
    private const int IDYES = 6;
    private const int IDNO = 7;

    /// <summary>種別の値。<b>前面に出すための印（最前面・前景）を必ず含める。</b></summary>
    internal static uint Flags(FrontButtons buttons, FrontIcon icon) =>
        (buttons switch
        {
            FrontButtons.YesNo => MB_YESNO,
            FrontButtons.YesNoCancel => MB_YESNOCANCEL,
            FrontButtons.RetryCancel => MB_RETRYCANCEL,
            _ => MB_OK,
        })
        | (icon switch
        {
            FrontIcon.Error => MB_ICONERROR,
            FrontIcon.Warning => MB_ICONWARNING,
            _ => MB_ICONINFORMATION,
        })
        | MB_SETFOREGROUND | MB_TOPMOST;

    /// <summary>押されたものへ読み替える。想定外（閉じたなど）は <see cref="FrontResult.Cancel"/>。</summary>
    internal static FrontResult ToResult(int id) => id switch
    {
        IDOK => FrontResult.Ok,
        IDYES => FrontResult.Yes,
        IDNO => FrontResult.No,
        IDRETRY => FrontResult.Retry,
        _ => FrontResult.Cancel,
    };

    internal static FrontResult Show(string text, FrontButtons buttons = FrontButtons.Ok, FrontIcon icon = FrontIcon.Information)
    {
        try
        {
            return ToResult(MessageBoxW(IntPtr.Zero, text, "Kado", Flags(buttons, icon)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return FrontResult.Cancel;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
