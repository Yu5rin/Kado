using Microsoft.Data.Sqlite;

namespace Kado.Presentation.Tests;

/// <summary>
/// 「カレンダー一覧を何回 DB から読んだか」を数える仕掛け。
/// <para>
/// 一時ビューで <c>calendars</c> を覆い、1行読むたびに関数を呼ばせる。カレンダーが
/// ちょうど1件のワークスペースで使うこと（行数＝読んだ回数になる）。書き込みはしない。
/// </para>
/// </summary>
internal sealed class SqlCounter : IDisposable
{
    private readonly SqliteConnection _connection;
    private int _calls;

    private SqlCounter(SqliteConnection connection) => _connection = connection;

    /// <summary>カレンダー一覧を読んだ回数。</summary>
    public int CalendarReads => _calls;

    public static SqlCounter Attach(SqliteConnection connection)
    {
        var counter = new SqlCounter(connection);

        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM calendars;";
            var rows = Convert.ToInt32(count.ExecuteScalar());
            if (rows != 1) throw new InvalidOperationException($"カレンダーが {rows} 件ある。1件で使うこと");
        }

        connection.CreateFunction("kado_probe", () =>
        {
            Interlocked.Increment(ref counter._calls);
            return 1;
        });

        using var create = connection.CreateCommand();
        create.CommandText =
            "CREATE TEMP VIEW calendars AS SELECT * FROM main.calendars WHERE kado_probe() = 1;";
        create.ExecuteNonQuery();
        return counter;
    }

    public void Dispose()
    {
        using var drop = _connection.CreateCommand();
        drop.CommandText = "DROP VIEW IF EXISTS temp.calendars;";
        drop.ExecuteNonQuery();
    }
}
