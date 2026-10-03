namespace Kado.Presentation.Tests;

/// <summary>
/// Linux でだけ走らせるテスト。Windows ではスキップ（理由つき）として数える。
/// <para>
/// 環境変数 <c>TZ</c> を読んでタイムゾーンを決めるのは Linux の .NET だけ。Windows の .NET は
/// OS（レジストリ）から読むので、<c>TZ</c> を書き換えても変わらず、確かめたいことを再現できない。
/// 黙って成功扱いにせず、スキップとして残す。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux 専用（Windows の .NET は TZ を読まないので、TZ の書き換えでは再現できない）";
        }
    }
}
