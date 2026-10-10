using System.Runtime.InteropServices;

namespace Kado.App.Shell;

/// <summary>
/// キーボードとマウスの操作が無い時間（自動更新が「手が空いたとき」を決めるのに使う）。
/// <para>
/// <c>GetLastInputInfo</c> は他のアプリを触っていても更新される。時刻は <c>GetTickCount</c> と同じ
/// 32ビットのミリ秒で、約49日で一周する。引き算は一周をまたいでも合うようにしてある（<see cref="Between"/>）。
/// </para>
/// </summary>
internal static class UserIdle
{
    /// <summary>
    /// いま操作が無い時間。<b>取れなかったときは null</b>（手が空いているとは言わない）。
    /// </summary>
    internal static TimeSpan? Current()
    {
        try
        {
            var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };

            if (!NativeMethods.GetLastInputInfo(ref info)) return null;

            // GetTickCount と同じ32ビット。GetTickCount64 の下位32ビットがそれに当たる
            return Between(unchecked((uint)Environment.TickCount64), info.dwTime);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 32ビットのミリ秒の時刻どうしの差。一周（約49日）をまたいでも正しい。
    /// </summary>
    /// <param name="nowTick">いまの時刻。</param>
    /// <param name="lastInputTick">最後の操作の時刻。</param>
    internal static TimeSpan Between(uint nowTick, uint lastInputTick) =>
        TimeSpan.FromMilliseconds(unchecked(nowTick - lastInputTick));
}
