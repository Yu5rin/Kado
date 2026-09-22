namespace Kado.Core.WorkingDays;

/// <summary>
/// タスク期限の表示種別。UI 側の色分けはこの値で決める。
/// </summary>
public enum DueKind
{
    /// <summary>実働日で数えられた残日数（例「残り 3実働日」）。</summary>
    WorkingDays,

    /// <summary>実働日データの範囲外のため暦日に倒した残日数（例「残り 189日」）。</summary>
    CalendarDays,

    /// <summary>期限が今日、あるいは実働日換算で今日が最後（例「今日まで」）。</summary>
    Today,

    /// <summary>期限を過ぎている（例「3実働日 遅れ」）。UI では赤で表示する。</summary>
    Overdue,
}

/// <summary>
/// 期限表示の組み立て結果。
/// </summary>
/// <param name="Text">画面にそのまま出せる文字列。</param>
/// <param name="Kind">表示種別。色分けに使う。</param>
/// <param name="EffectiveDue">
/// 実際に計算の基準とした期限日。期限日が非稼働日だった場合は直前の実働日に寄せた日付が入る。
/// 寄せていない場合は元の期限日と同じ。
/// </param>
/// <param name="IsSnapped">期限日が非稼働日で、直前の実働日に寄せたかどうか。</param>
/// <param name="Amount">
/// 残日数・超過日数の絶対値。<see cref="DueKind.Today"/> のときは 0。
/// 単位は <see cref="Kind"/> が <see cref="DueKind.CalendarDays"/> なら暦日、それ以外は実働日。
/// ただし <see cref="DueKind.Overdue"/> は暦日に倒すことがあるため <see cref="IsCalendarUnit"/> で判断する。
/// </param>
/// <param name="IsCalendarUnit">
/// <see cref="Amount"/> の単位が暦日かどうか。true なら「日」、false なら「実働日」。
/// ツールチップで「この期間は実働日データが未登録」と補足する判断にも使う。
/// </param>
public sealed record DueText(
    string Text,
    DueKind Kind,
    DateOnly EffectiveDue,
    bool IsSnapped,
    int Amount,
    bool IsCalendarUnit)
{
    public override string ToString() => Text;
}

/// <summary>済んだタスクが期限に間に合ったか。</summary>
public enum DoneKind
{
    /// <summary>期限どおり。</summary>
    OnTime,

    /// <summary>期限より前に済ませた。</summary>
    Early,

    /// <summary>期限を過ぎてから済ませた。UI では控えめな赤で出す。</summary>
    Late,
}

/// <summary>
/// 済んだタスクの結果。
/// <para>「2実働日 遅れて完了」のような一行と、その内訳。</para>
/// </summary>
/// <param name="Text">画面に出す文字列。</param>
/// <param name="Kind">間に合ったかどうか。</param>
/// <param name="Days">期限との差。単位は <paramref name="IsCalendarUnit"/> で決まる。</param>
/// <param name="IsCalendarUnit">暦日で数えたか。false なら実働日。</param>
public sealed record DoneText(string Text, DoneKind Kind, int Days, bool IsCalendarUnit);
