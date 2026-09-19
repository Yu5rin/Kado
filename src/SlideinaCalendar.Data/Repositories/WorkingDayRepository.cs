using Dapper;
using Microsoft.Data.Sqlite;
using SlideinaCalendar.Core.Import;
using SlideinaCalendar.Core.WorkingDays;

namespace SlideinaCalendar.Data.Repositories;

/// <summary>
/// 実働日とマイルストーンの読み書き。
/// <para>
/// Core の <see cref="WorkingDayCalendar"/> をそのまま出し入れできるようにしてある。
/// 判定や集計のロジックは Core にあり、ここは永続化だけを担う。
/// </para>
/// </summary>
public sealed class WorkingDayRepository(SqliteConnection connection)
{
    private const string WorkingDayKind = "working_day";
    private const string MilestoneKind = "milestone";

    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>保存されている内容からカレンダーを組み立てる。</summary>
    public WorkingDayCalendar Load()
    {
        var days = _connection.Query<string>("SELECT date FROM working_days ORDER BY date;")
            .Select(SqliteTypeHandlers.TryParseDate)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToArray();

        var milestones = _connection.Query<(string Date, string Name, string? Version)>(
                "SELECT date, name, source_version FROM milestones ORDER BY date;")
            .Select(r => (Date: SqliteTypeHandlers.TryParseDate(r.Date), r.Name, r.Version))
            .Where(r => r.Date is not null)
            .Select(r => new Milestone(r.Date!.Value, r.Name, r.Version))
            .ToArray();

        var (workStart, workEnd) = LoadRange(WorkingDayKind);
        var (msStart, msEnd) = LoadRange(MilestoneKind);

        return WorkingDayCalendar.Create(days, workStart, workEnd, milestones, msStart, msEnd);
    }

    /// <summary>
    /// カレンダーの内容で全面的に置き換える。
    /// <para>
    /// <b>期間単位の置き換えはここでは行わない。</b>それは
    /// <see cref="WorkingDayCalendar.Merge"/> の仕事で、呼び出し側は
    /// 「読み込む → Merge する → 保存する」の順で扱う。永続化と併合の責務を分けておくと、
    /// 併合の規則を Core 側のテストだけで担保できる。
    /// </para>
    /// </summary>
    public void Save(WorkingDayCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        using var transaction = _connection.BeginTransaction();

        _connection.Execute("DELETE FROM working_days;", transaction: transaction);
        _connection.Execute("DELETE FROM milestones;", transaction: transaction);
        _connection.Execute("DELETE FROM data_ranges;", transaction: transaction);

        foreach (var day in calendar.Days)
        {
            _connection.Execute(
                "INSERT INTO working_days (date) VALUES (@date);",
                new { date = SqliteTypeHandlers.ToText(day) }, transaction);
        }

        foreach (var milestone in calendar.AllMilestones)
        {
            // 同じ日に同じ名前が二重に来ても落とさない。取り込み元の重複はここで吸収する
            _connection.Execute(
                """
                INSERT INTO milestones (date, name, source_version)
                VALUES (@date, @name, @version)
                ON CONFLICT (date, name) DO UPDATE SET source_version = excluded.source_version;
                """,
                new
                {
                    date = SqliteTypeHandlers.ToText(milestone.Date),
                    name = milestone.Name,
                    version = milestone.SourceVersion,
                }, transaction);
        }

        SaveRange(WorkingDayKind, calendar.RangeStart, calendar.RangeEnd, transaction);
        SaveRange(MilestoneKind, calendar.MilestoneRangeStart, calendar.MilestoneRangeEnd, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// 取り込み結果を反映する。既存を読み、期間単位で置き換えてから保存する。
    /// <para>古いファイルを誤って読み込んでも過去データが消えない（要件書 4.1）。</para>
    /// </summary>
    public WorkingDayCalendar Apply(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var merged = Load().Merge(result);
        Save(merged);
        return merged;
    }

    /// <summary>登録されている稼働日の件数。</summary>
    public int Count() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM working_days;");

    /// <summary>登録されているマイルストーンの件数。</summary>
    public int MilestoneCount() => _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM milestones;");

    private (DateOnly? Start, DateOnly? End) LoadRange(string kind)
    {
        var row = _connection.QuerySingleOrDefault<(string Start, string End)?>(
            "SELECT start_date, end_date FROM data_ranges WHERE kind = @kind;", new { kind });

        return row is null
            ? (null, null)
            : (SqliteTypeHandlers.TryParseDate(row.Value.Start), SqliteTypeHandlers.TryParseDate(row.Value.End));
    }

    private void SaveRange(string kind, DateOnly? start, DateOnly? end, SqliteTransaction transaction)
    {
        if (start is null || end is null) return;

        _connection.Execute(
            """
            INSERT INTO data_ranges (kind, start_date, end_date)
            VALUES (@kind, @start, @end)
            ON CONFLICT (kind) DO UPDATE SET
                start_date = excluded.start_date, end_date = excluded.end_date;
            """,
            new
            {
                kind,
                start = SqliteTypeHandlers.ToText(start.Value),
                end = SqliteTypeHandlers.ToText(end.Value),
            }, transaction);
    }
}
