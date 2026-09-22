namespace Kado.Core.Recurrence;

/// <summary>
/// 繰り返し種別ごとの判定ロジック。
/// <para>
/// 種別を増やすときはこれを実装し、<see cref="RecurrenceRule.RegisterPattern"/> で登録する。
/// 既存の種別に手を入れずに追加できるようにしてある（要件書 11 章で対応種別が未確定のため）。
/// </para>
/// </summary>
public interface IRecurrencePattern
{
    /// <summary>指定文字列での種別名（<c>FREQ=</c> の値。例 <c>WEEKLY</c>）。</summary>
    string Frequency { get; }

    /// <summary>何回ごとか。毎回なら 1。</summary>
    int Interval { get; }

    /// <summary>
    /// <paramref name="date"/> が繰り返しに該当するか。
    /// 期間（<c>seriesStart</c> 以降か、UNTIL を過ぎていないか）の判定は
    /// <see cref="RecurrenceRule"/> 側で済ませてあるので、ここでは周期だけを見ればよい。
    /// </summary>
    bool Matches(DateOnly date, DateOnly seriesStart);

    /// <summary>「毎週 火曜」のような表示用の文字列。</summary>
    string ToLabel(DateOnly? seriesStart);

    /// <summary><c>FREQ=WEEKLY;BYDAY=TU</c> のような指定文字列に戻す（UNTIL は含めない）。</summary>
    string ToSpec();
}
