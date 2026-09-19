namespace SlideinaCalendar.Data.Migrations;

/// <summary>
/// スキーマの版。<see cref="Version"/> の昇順に適用される。
/// <para>
/// 一度リリースした版の <see cref="Sql"/> は<b>書き換えない</b>。既存のデータベースには
/// 適用済みなので、変更しても反映されず、新規のものとの間で差が生まれてしまう。
/// 変更が要るときは新しい版を足す。
/// </para>
/// </summary>
/// <param name="Version">版番号。1 から連番。</param>
/// <param name="Description">何をする版か。</param>
/// <param name="Sql">適用する SQL。</param>
public sealed record Migration(int Version, string Description, string Sql);
