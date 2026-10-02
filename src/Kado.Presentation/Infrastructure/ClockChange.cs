namespace Kado.Presentation.Infrastructure;

/// <summary>
/// 日付を確かめ直したくなるシステム側の出来事。
/// <para>
/// 中身は <c>SystemEvents.PowerModeChanged</c> と <c>SystemEvents.TimeChanged</c> を
/// 画面側で読み替えたもの。WPF の型を持ち込まずに、判断だけをテストできるようにしてある。
/// </para>
/// </summary>
public enum ClockChange
{
    /// <summary>スリープや休止から復帰した。</summary>
    Resume,

    /// <summary>スリープや休止に入る。</summary>
    Suspend,

    /// <summary>電源の状態が変わった（バッテリーと AC の切り替えなど）。時刻には関係しない。</summary>
    PowerStatus,

    /// <summary>システムの時計が変更された（手動の変更、時刻同期、タイムゾーンの変更）。</summary>
    TimeChanged,
}

/// <summary>
/// 1分のタイマーを待たずに、すぐ日付を確かめ直すかどうかの判断。
/// <para>
/// 眠っているあいだタイマーは止まっているので、翌朝に起こしても次の1分までは
/// 前日の「今日」のままになる。時計を変えたときも同じ。
/// </para>
/// </summary>
public static class ClockChangeRules
{
    /// <summary>確かめ直すか。時刻が飛びうる出来事（復帰・時計の変更）だけ。</summary>
    public static bool ShouldRecheck(ClockChange change) =>
        change is ClockChange.Resume or ClockChange.TimeChanged;
}
