namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// 編集画面のカレンダー欄・タスクリスト欄に並べる1件。
/// <para>
/// 自分で作ったカレンダーは ID が <c>local:</c> で始まる機械的な文字列になり、
/// 名前とは別物になる。ID をそのまま並べると画面に <c>local:9f1c…</c> と出てしまうので、
/// 見せる名前と保存する ID を分けて持つ。
/// </para>
/// </summary>
/// <param name="Id">保存する値。カレンダー ID またはタスクリスト ID。</param>
/// <param name="Name">画面に出す名前。</param>
public sealed record SourceChoice(string Id, string Name)
{
    /// <summary>コンボの選択中の表示はこれが使われる。レコードの既定表現を出さない。</summary>
    public override string ToString() => Name;
}
