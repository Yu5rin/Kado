using System.Globalization;

namespace Kado.Core.WorkingDays;

/// <summary>
/// 実働日データと一緒に取り込まれる読み取り専用のマイルストーン。
/// <para>
/// 予定でもタスクでもない第3のデータ種別で、編集・削除はできず Google にも同期しない。
/// 種類名（<see cref="Name"/>）は配布 Excel の D 列に現れた文字列をそのまま保持する。
/// 固定4種（仕様期限／1次GO／S中日程／M中日程）で決め打ちしないこと。将来名称が増えても
/// 取り込みが壊れないようにするための設計上の約束である。
/// </para>
/// </summary>
/// <param name="Date">マイルストーンの日付。</param>
/// <param name="Name">種類名。取り込み元の文字列をそのまま保持する。</param>
/// <param name="SourceVersion">取り込み元ファイルのバージョン（例 <c>Ver．25.1</c>）。不明な場合は null。</param>
public sealed record Milestone(DateOnly Date, string Name, string? SourceVersion = null)
{
    public override string ToString() =>
        $"{Date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} {Name}";
}
