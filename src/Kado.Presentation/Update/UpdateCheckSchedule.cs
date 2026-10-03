namespace Kado.Presentation.Update;

/// <summary>
/// 常駐しているあいだの、新しい版の確認の段取り。
/// <para>
/// 確認は、以前は起動したときと押したときだけだった。トレイに入ったまま何日も動かし続ける
/// 使い方（既定）では、起動した版のまま、新しい版に一度も気づかない。
/// <b>起動から24時間ごと</b>に、静かに確かめる。スリープから戻って日付が変わっていたときは、
/// 24時間を待たずに確かめる（眠っているあいだ、タイマーは止まっている）。
/// </para>
/// <para>
/// 確認が失敗したとき（会社の回線で GitHub に届かない、など）は、24時間を待たず、2時間後に出し直す。
/// ずっと届かない回線で、30分ごとに叩き続けることはしない。
/// 時計を渡して使うので、試験では実時間を待たない。
/// </para>
/// </summary>
public sealed class UpdateCheckSchedule
{
    /// <summary>確認の間隔。起動から数える。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>確認が失敗したあと、出し直すまでの間隔。</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(2);

    private DateTime? _lastCheckedAt;
    private DateTime? _nextDue;

    /// <summary>確認を終えた（最新だった・新しい版が見つかった）。次は <see cref="Interval"/> 後。</summary>
    public void MarkChecked(DateTime now)
    {
        _lastCheckedAt = now;
        _nextDue = now + Interval;
    }

    /// <summary>確認が通らなかった。<see cref="RetryAfterFailure"/> 後に出し直す。</summary>
    public void MarkFailed(DateTime now) => _nextDue = now + RetryAfterFailure;

    /// <summary>いま確かめるべきか。</summary>
    /// <param name="now">いまの時刻（ローカル）。</param>
    /// <param name="afterResume">スリープから戻った直後か。</param>
    public bool IsDue(DateTime now, bool afterResume = false)
    {
        // まだ一度も確かめていない
        if (_nextDue is not { } due) return true;

        if (now >= due) return true;

        // 時計を過去へ戻された。次の予定が24時間より先になっていたら、待ち続けずに確かめ直す
        if (due - now > Interval) return true;

        // 日をまたいで眠っていた。24時間を待たず、起きたらすぐ
        return afterResume && _lastCheckedAt is { } last && now.Date != last.Date;
    }
}
