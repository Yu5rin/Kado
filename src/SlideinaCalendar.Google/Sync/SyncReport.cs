namespace SlideinaCalendar.Google.Sync;

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

    /// <summary>差分では追いつけず、全部取り直したか。</summary>
    public bool FullResync { get; init; }

    /// <summary>伝えきれなかったことがら。止めるほどではないが、黙らせない。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>何か変わったか。変わっていなければ画面に出さない。</summary>
    public bool HasChanges =>
        CreatedLocal + UpdatedLocal + DeletedLocal +
        CreatedRemote + UpdatedRemote + DeletedRemote + Relinked > 0;

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
        FullResync = left.FullResync || right.FullResync,
        Warnings = [.. left.Warnings, .. right.Warnings],
    };
}
