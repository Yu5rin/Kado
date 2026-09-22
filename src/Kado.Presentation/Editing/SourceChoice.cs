namespace Kado.Presentation.Editing;

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
/// <param name="Notifies">
/// このカレンダーは既定で知らせるか（左パネルのベルの状態）。
/// <para>
/// タスクリストは通知の対象にしていないので常に既定値（true）のまま。予定の編集画面が
/// 「カレンダーに従う」の結果（知らせる／知らせない）を添えて出すのに使う。
/// 既定値つきの任意引数にしてあるのは、通知に関係しない呼び出し側（タスクリスト欄など）
/// を直さずに済ませるため。
/// </para>
/// </param>
public sealed record SourceChoice(string Id, string Name, bool Notifies = true)
{
    /// <summary>コンボの選択中の表示はこれが使われる。レコードの既定表現を出さない。</summary>
    public override string ToString() => Name;
}
