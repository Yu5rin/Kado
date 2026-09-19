using System.Text;

namespace SlideinaCalendar.Data.Import;

/// <summary>取り込みログの重み。</summary>
public enum ImportLogLevel
{
    /// <summary>そのまま取り込めた。件数の記録など。</summary>
    Info,

    /// <summary>取り込めたが、解釈を補ったり一部を落としたりした。目視の確認が要る。</summary>
    Warning,

    /// <summary>取り込めなかった。データが失われている。</summary>
    Error,
}

/// <summary>
/// 取り込みログの1行。
/// <para>
/// 旧データの移行は一度きりの操作で、やり直しが効かない。何がどう変換されたかを
/// 後から確認できるようにするため、判断が入った箇所はすべて残す（要件書 8 章）。
/// </para>
/// </summary>
/// <param name="Level">重み。</param>
/// <param name="Category">どの処理か（「タスク変換」「繰り返し」など）。</param>
/// <param name="Message">内容。</param>
/// <param name="TargetId">対象の識別子。元データを引き当てられるようにする。</param>
public sealed record ImportLogEntry(
    ImportLogLevel Level,
    string Category,
    string Message,
    string? TargetId = null)
{
    public override string ToString()
    {
        var mark = Level switch
        {
            ImportLogLevel.Error => "エラー",
            ImportLogLevel.Warning => "警告",
            _ => "情報",
        };

        var sb = new StringBuilder();
        sb.Append('[').Append(mark).Append("] ").Append(Category).Append(": ").Append(Message);
        if (TargetId is not null) sb.Append("（対象: ").Append(TargetId).Append('）');
        return sb.ToString();
    }
}
