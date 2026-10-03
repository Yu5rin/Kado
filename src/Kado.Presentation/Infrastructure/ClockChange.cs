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

    /// <summary>
    /// 少し待ってから、裏の同期を1回起こすか。スリープや休止から復帰したときだけ。
    /// <para>
    /// 眠っているあいだタイマーは止まっているので、戻っても次の間隔が来るまで同期が走らない
    /// （失敗で最長2時間まで延びていることもある）。時計を変えただけでは、ネットワークは変わらない。
    /// </para>
    /// </summary>
    public static bool ShouldSyncAfter(ClockChange change) => change is ClockChange.Resume;

    /// <summary>
    /// タイムゾーンと書式のキャッシュを捨てるか。時計を変えられたとき（タイムゾーンの変更を含む）と、
    /// 復帰したとき（眠っているあいだに自動で変わっていることがある）。
    /// </summary>
    public static bool ShouldRefreshCaches(ClockChange change) =>
        change is ClockChange.TimeChanged or ClockChange.Resume;
}

/// <summary>
/// 時計・タイムゾーンまわりのキャッシュ。
/// <para>
/// .NET は <see cref="TimeZoneInfo.Local"/> と現在の <see cref="System.Globalization.CultureInfo"/> の
/// 書式を最初に読んだ値のまま持ち続ける。トレイに常駐して何日も動くアプリは、
/// タイムゾーンを変えられても（出張・夏時間・設定の変更）古いままの時刻で動いてしまう。
/// 時計の変更を受けたら、これを呼んで捨てる。
/// </para>
/// </summary>
public static class ClockCaches
{
    /// <summary>
    /// タイムゾーンと、呼んだスレッドの現在のカルチャの書式のキャッシュを捨てる。
    /// <para>
    /// カルチャのほうは<b>呼んだスレッドのもの</b>に効く。画面のスレッドから呼ぶこと
    /// （システムの通知が来るスレッドで呼んでも、画面のスレッドのカルチャには効かない）。
    /// </para>
    /// </summary>
    public static void Refresh()
    {
        TimeZoneInfo.ClearCachedData();
        System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
    }
}
