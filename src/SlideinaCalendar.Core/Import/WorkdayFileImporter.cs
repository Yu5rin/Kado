using System.Globalization;
using ClosedXML.Excel;
using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Core.Import;

/// <summary>
/// 会社配布 Excel（実働日ファイル.xlsx）の取り込み。
/// <para>ファイル構造（要件書 4.1）:</para>
/// <list type="table">
///   <item><term>1〜3行目</term><description>バージョン・参照先パス・ファイル名。バージョンだけ記録する</description></item>
///   <item><term>B列（5行目以降）</term><description>稼働日の日付</description></item>
///   <item><term>C列</term><description>実働日の通し番号。<b>読み飛ばす</b>（アプリ側で再計算する）</description></item>
///   <item><term>D列</term><description>マイルストーン名称。同じ行の日付のマイルストーンとして登録する</description></item>
///   <item><term>A列 / L〜N列</term><description>連番、別系統の稼働日リスト（2002年〜）。使わない</description></item>
///   <item><term>E〜J列</term><description>区分別リードタイム表。使わない</description></item>
/// </list>
/// <para>
/// マイルストーン名称は固定4種で決め打ちしない。D列に現れた文字列をそのまま種類として登録する
/// （将来の名称追加で取り込みが壊れないようにするため）。
/// </para>
/// <para>
/// 不正な行は例外を投げず <see cref="ImportResult.Warnings"/> に積んで処理を続ける。
/// </para>
/// </summary>
public sealed class WorkdayFileImporter
{
    /// <summary>データが始まる行。1〜4行目はヘッダ領域。</summary>
    private const int FirstDataRow = 5;

    /// <summary>ヘッダ（バージョン等）を探す範囲。</summary>
    private const int HeaderRowCount = 3;

    private const int ColumnWorkingDay = 2;   // B列: 稼働日
    private const int ColumnMilestone = 4;    // D列: マイルストーン名称

    /// <summary>ヘッダでバージョン欄を見つけるための見出し語。</summary>
    private const string VersionLabel = "バージョン";

    /// <summary>
    /// Excel を読み込む。
    /// </summary>
    /// <param name="xlsx">xlsx ファイルのストリーム。</param>
    /// <exception cref="InvalidDataException">
    /// ワークシートが無い、または稼働日が1件も読めなかった場合。
    /// 行単位の不正は警告として扱うが、稼働日が皆無のファイルは実働日データとして成立しないため例外にする。
    /// </exception>
    public ImportResult Import(Stream xlsx)
    {
        ArgumentNullException.ThrowIfNull(xlsx);

        using var workbook = new XLWorkbook(xlsx);
        var sheet = SelectSheet(workbook);

        var warnings = new List<string>();
        var version = ReadVersion(sheet, warnings);

        var workingDays = new List<DateOnly>();
        var seen = new HashSet<DateOnly>();
        var milestones = new List<Milestone>();

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var dataStarted = false;

        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var dateCell = sheet.Cell(row, ColumnWorkingDay);
            var nameCell = sheet.Cell(row, ColumnMilestone);

            var date = TryReadDate(dateCell);
            var name = ReadText(nameCell);

            if (date is null)
            {
                if (!dateCell.IsEmpty())
                {
                    // データが始まる前の非日付セルは見出し行（B5 の見出しなど）なので黙って読み飛ばす。
                    // データが始まったあとに出てきたものだけ警告する。
                    if (dataStarted)
                    {
                        warnings.Add($"{row}行目: B列が日付として読めません（'{ReadText(dateCell)}'）。行を読み飛ばしました。");
                    }
                }
                else if (name is not null && dataStarted)
                {
                    warnings.Add($"{row}行目: D列に'{name}'がありますが、同じ行のB列に日付がありません。読み飛ばしました。");
                }
                continue;
            }

            dataStarted = true;

            if (!seen.Add(date.Value))
            {
                warnings.Add(
                    $"{row}行目: 稼働日 {date.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} が重複しています。2件目以降は無視しました。");
            }
            else
            {
                workingDays.Add(date.Value);
            }

            // C列（実働日の通し番号）は意図的に読まない。アプリ側で再計算する。
            if (name is not null)
            {
                milestones.Add(new Milestone(date.Value, name, version.Length > 0 ? version : null));
            }
        }

        if (workingDays.Count == 0)
        {
            throw new InvalidDataException(
                $"稼働日を1件も読み取れませんでした（シート '{sheet.Name}' の{FirstDataRow}行目以降のB列）。ファイル形式を確認してください。");
        }

        workingDays.Sort();
        milestones.Sort((a, b) => a.Date.CompareTo(b.Date));

        return new ImportResult(
            Version: version,
            WorkingDayRangeStart: workingDays[0],
            WorkingDayRangeEnd: workingDays[^1],
            MilestoneRangeStart: milestones.Count > 0 ? milestones[0].Date : null,
            MilestoneRangeEnd: milestones.Count > 0 ? milestones[^1].Date : null,
            WorkingDays: workingDays,
            Milestones: milestones,
            Warnings: warnings);
    }

    /// <summary>
    /// 読み込むシートを決める。「実働日」があればそれを、無ければ先頭シートを使う。
    /// シート名の変更で取り込みが止まらないようにするための保険。
    /// </summary>
    private static IXLWorksheet SelectSheet(XLWorkbook workbook)
    {
        if (workbook.Worksheets.TryGetWorksheet("実働日", out var named)) return named;

        var first = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidDataException("ワークシートが1枚もありません。");
        return first;
    }

    /// <summary>
    /// ヘッダからバージョンを読む。見出し「バージョン」を探し、その右にある最初の値を採る。
    /// 実ファイルでは B1 が見出し、D1 が値になっている。
    /// </summary>
    private static string ReadVersion(IXLWorksheet sheet, List<string> warnings)
    {
        var lastColumn = Math.Max(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 1);

        for (var row = 1; row <= HeaderRowCount; row++)
        {
            for (var col = 1; col <= lastColumn; col++)
            {
                var label = ReadText(sheet.Cell(row, col));
                if (label is null || !label.Contains(VersionLabel, StringComparison.Ordinal)) continue;

                for (var next = col + 1; next <= lastColumn; next++)
                {
                    var value = ReadText(sheet.Cell(row, next));
                    if (value is not null) return value;
                }
            }
        }

        warnings.Add($"ヘッダ（1〜{HeaderRowCount}行目）にバージョンが見つかりませんでした。更新判定には使えません。");
        return string.Empty;
    }

    /// <summary>日付として読めれば <see cref="DateOnly"/>、読めなければ null。</summary>
    private static DateOnly? TryReadDate(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;

        // 数値を日付シリアルとして取り違えないよう、書式が日付のセルだけを対象にする
        if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var value))
        {
            return DateOnly.FromDateTime(value);
        }
        return null;
    }

    /// <summary>前後の空白を落とした文字列。空白のみ・空セルは null。</summary>
    private static string? ReadText(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        var text = cell.GetFormattedString().Trim();
        return text.Length == 0 ? null : text;
    }
}
