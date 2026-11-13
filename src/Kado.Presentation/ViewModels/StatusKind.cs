namespace Kado.Presentation.ViewModels;

/// <summary>
/// 画面の下に出す知らせの種類。
/// <para>
/// 成功は数秒で消える（カーソルが載っているあいだは消えない）。失敗は、×を押すか次の知らせが出るまで残る。
/// 席を外しているあいだに出た失敗が、読まれないまま消えるのを防ぐ。
/// </para>
/// </summary>
public enum StatusKind
{
    /// <summary>うまくいった・お知らせ。</summary>
    Success,

    /// <summary>失敗した・断った。</summary>
    Failure,
}
