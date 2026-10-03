namespace Kado.Google.Sync;

/// <summary>
/// 一度の同期で何が起きたか。
/// <para>
/// 画面の「同期」表示と記録に出す。件数だけでなく、取り直しが起きたかも持つ。
/// 毎回 <see cref="FullResync"/> が立つなら、差分の印が保てていない。
/// </para>
/// </summary>
public sealed record SyncReport
{
    /// <summary>相手から取り込んで作った件数。</summary>
    public int CreatedLocal { get; init; }

    /// <summary>相手の内容で書き換えた件数。</summary>
    public int UpdatedLocal { get; init; }

    /// <summary>相手が消したので、こちらでも消した件数。</summary>
    public int DeletedLocal { get; init; }

    /// <summary>相手に作った件数。</summary>
    public int CreatedRemote { get; init; }

    /// <summary>相手へ送った件数。</summary>
    public int UpdatedRemote { get; init; }

    /// <summary>こちらで消したので、相手でも消した件数。</summary>
    public int DeletedRemote { get; init; }

    /// <summary>
    /// すでに相手にあったものと結び直した件数。
    /// <para>結び直さないと、同じ予定が2件に増える。</para>
    /// </summary>
    public int Relinked { get; init; }

    /// <summary>
    /// 別のカレンダー／タスクリストへ移した件数。
    /// <para>
    /// 編集画面で入れ先を変えたものを <c>events.move</c> / <c>tasks.move</c> で運んだ数。
    /// 消して作り直していないので、ゲスト・会議 URL・添付・色は保たれている。
    /// </para>
    /// </summary>
    public int Moved { get; init; }

    /// <summary>差分では追いつけず、全部取り直したか。</summary>
    public bool FullResync { get; init; }

    /// <summary>
    /// カレンダー・タスクリストの一覧そのものが変わったか（Google 側で消えた、など）。
    /// <para>
    /// カレンダー一覧の取り込み（<c>GoogleSyncService.ImportCalendarListAsync</c>）は、
    /// 見つけた・更新したぶんは <see cref="CreatedLocal"/> / <see cref="UpdatedLocal"/> で
    /// 数えるが、<b>Google から消えたので一覧から外した</b>ものはどちらにも数えない
    /// （警告文にだけ残す）。ここだけ数から漏れると、消えたカレンダーがある同期でも
    /// 「何も変わっていない」と判定され、左パネルが古いまま残ってしまう。
    /// </para>
    /// </summary>
    public bool SourcesChanged { get; init; }

    /// <summary>
    /// Google が混み合っていて（呼びすぎ・5xx）、待って出し直してもだめだったので、一部を次回に回したか。
    /// <para>
    /// <b>手元は壊れていない</b>ので失敗ではない。残りは次の同期でまた送る。
    /// </para>
    /// </summary>
    public bool Deferred { get; init; }

    /// <summary>
    /// <see cref="Deferred"/> のうち、<b>呼びすぎ</b>（429・呼びすぎの 403）と言われたもの。
    /// <para>
    /// すぐ次を叩いても同じ結果になる。裏の定期同期は、これが立ったら間隔を延ばす
    /// （<c>SyncViewModel.SyncQuietlyAsync</c>）。1つのカレンダーの5xxでは立てない
    /// （その不調で、ほかのカレンダーの同期まで遅くしない）。
    /// </para>
    /// </summary>
    public bool Throttled { get; init; }

    /// <summary>
    /// <see cref="Deferred"/> のときに出す警告。<b>同じ文で出す</b>（画面は同じ文を重ねない。
    /// カレンダーごとに出しても1行にまとまる）。
    /// </summary>
    public const string BusyWarning = "Google が混み合っていたので、一部を次回に回しました";

    /// <summary>伝えきれなかったことがら。止めるほどではないが、黙らせない。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>何か変わったか。変わっていなければ画面に出さない。</summary>
    public bool HasChanges =>
        CreatedLocal + UpdatedLocal + DeletedLocal +
        CreatedRemote + UpdatedRemote + DeletedRemote + Relinked + Moved > 0 ||
        SourcesChanged;

    /// <summary>取り込んだ件数の合計。</summary>
    public int PulledCount => CreatedLocal + UpdatedLocal + DeletedLocal;

    /// <summary>送った件数の合計。</summary>
    public int PushedCount => CreatedRemote + UpdatedRemote + DeletedRemote;

    /// <summary>画面に出す短い文。</summary>
    public string Summary() => HasChanges
        ? $"取り込み {PulledCount} 件 ／ 送信 {PushedCount} 件"
        : "変更なし";

    /// <summary>2つの結果を足す。カレンダーごとの結果をまとめるのに使う。</summary>
    public static SyncReport operator +(SyncReport left, SyncReport right) => new()
    {
        CreatedLocal = left.CreatedLocal + right.CreatedLocal,
        UpdatedLocal = left.UpdatedLocal + right.UpdatedLocal,
        DeletedLocal = left.DeletedLocal + right.DeletedLocal,
        CreatedRemote = left.CreatedRemote + right.CreatedRemote,
        UpdatedRemote = left.UpdatedRemote + right.UpdatedRemote,
        DeletedRemote = left.DeletedRemote + right.DeletedRemote,
        Relinked = left.Relinked + right.Relinked,
        Moved = left.Moved + right.Moved,
        FullResync = left.FullResync || right.FullResync,
        SourcesChanged = left.SourcesChanged || right.SourcesChanged,
        Deferred = left.Deferred || right.Deferred,
        Throttled = left.Throttled || right.Throttled,
        Warnings = [.. left.Warnings, .. right.Warnings],
    };
}
