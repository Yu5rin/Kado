using Microsoft.Data.Sqlite;
using SlideinaCalendar.Core.WorkingDays;
using SlideinaCalendar.Data;

namespace SlideinaCalendar.Presentation.Tests;

/// <summary>テスト用のワークスペース。メモリ上のデータベースを使う。</summary>
internal sealed class TestWorkspace : IDisposable
{
    private readonly SqliteConnection _connection;

    public CalendarWorkspace Workspace { get; }

    private TestWorkspace(SqliteConnection connection, CalendarWorkspace workspace)
    {
        _connection = connection;
        Workspace = workspace;
    }

    /// <summary>
    /// 2026年9月の稼働日（土日と敬老の日・国民の休日・秋分の日を除く19日）を
    /// 登録したワークスペースを作る。Core のテストと同じ並びなので、
    /// 実働日まわりの期待値をそろえられる。
    /// </summary>
    /// <param name="withMilestones">
    /// マイルストーンも入れるか。入れると起動時に「inaCalendar」が作られ、その予定も
    /// 1件増える（日付の行は予定から組み立てるため）。要るテストだけが頼むようにする。
    /// </param>
    public static TestWorkspace Create(
        bool withWorkingDays = true,
        IReadOnlyDictionary<DateOnly, string>? holidays = null,
        bool withMilestones = false)
    {
        var connection = CalendarDatabase.OpenInMemory().ConnectAndMigrate();

        if (withWorkingDays)
        {
            // 稼働日から除く日。表示用の祝日名とは別物で、祝日でも稼働する場合がある
            var closedDays = new[]
            {
                new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23),
            };
            var days = Enumerable.Range(1, 30)
                .Select(d => new DateOnly(2026, 9, d))
                .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closedDays.Contains(d))
                .ToArray();

            new Data.Repositories.WorkingDayRepository(connection).Save(
                WorkingDayCalendar.Create(
                    days,
                    new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
                    withMilestones ? [new Milestone(new DateOnly(2026, 9, 14), "仕様期限", "Ver．26.1")] : [],
                    new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14)));
        }

        var holidaySource = holidays is null ? null : new HolidayTable(holidays);
        return new TestWorkspace(connection, new CalendarWorkspace(connection, holidaySource));
    }

    public void Dispose() => _connection.Dispose();
}
