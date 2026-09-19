using System.Text;
using SlideinaCalendar.Data.Models;

namespace SlideinaCalendar.Data.Import;

/// <summary>
/// 旧 inaCalendar のバックアップを読み込んだ結果。
/// </summary>
/// <param name="SourceApp">バックアップを書いたアプリ名。</param>
/// <param name="SourceVersion">バックアップ形式の版。</param>
/// <param name="ExportedAt">書き出された日時。</param>
/// <param name="Events">予定。</param>
/// <param name="Tasks">タスク。ToDo フラグの付いた予定から変換したものを含む。</param>
/// <param name="WorkingDays">稼働日。</param>
/// <param name="WorkingDayRangeStart">稼働日データの登録範囲の開始。</param>
/// <param name="WorkingDayRangeEnd">稼働日データの登録範囲の終了。</param>
/// <param name="Settings">設定。構造のある値は JSON のまま入る。</param>
/// <param name="Log">変換ログ。</param>
public sealed record LegacyImportResult(
    string SourceApp,
    int SourceVersion,
    DateTimeOffset? ExportedAt,
    IReadOnlyList<CalendarEvent> Events,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<DateOnly> WorkingDays,
    DateOnly? WorkingDayRangeStart,
    DateOnly? WorkingDayRangeEnd,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<ImportLogEntry> Log)
{
    /// <summary>警告の件数。</summary>
    public int WarningCount => Log.Count(e => e.Level == ImportLogLevel.Warning);

    /// <summary>エラーの件数。0 でなければデータが失われている。</summary>
    public int ErrorCount => Log.Count(e => e.Level == ImportLogLevel.Error);

    /// <summary>ToDo フラグから変換したタスクの件数。</summary>
    public int ConvertedTaskCount => Tasks.Count(t => t.Source == LegacyBackupImporter.ConvertedTaskSource);

    /// <summary>
    /// ログを人が読める形に整える。移行後にそのまま画面へ出したりファイルへ書いたりする。
    /// </summary>
    public string FormatLog()
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== 旧データの取り込み結果 ===");
        sb.AppendLine($"取り込み元 : {SourceApp}（形式 v{SourceVersion}）");
        if (ExportedAt is { } exported) sb.AppendLine($"書き出し日時: {exported:yyyy/MM/dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"予定        : {Events.Count} 件");
        sb.AppendLine($"タスク      : {Tasks.Count} 件（うち ToDo からの変換 {ConvertedTaskCount} 件）");
        sb.AppendLine($"稼働日      : {WorkingDays.Count} 件");
        if (WorkingDayRangeStart is { } s && WorkingDayRangeEnd is { } e)
        {
            sb.AppendLine($"稼働日の範囲: {s:yyyy/MM/dd} 〜 {e:yyyy/MM/dd}");
        }
        sb.AppendLine($"設定        : {Settings.Count} 項目");
        sb.AppendLine();
        sb.AppendLine($"警告 {WarningCount} 件 / エラー {ErrorCount} 件");

        if (Log.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- 明細 ---");
            foreach (var entry in Log) sb.AppendLine(entry.ToString());
        }

        return sb.ToString();
    }
}
