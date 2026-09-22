using Dapper;
using Microsoft.Data.Sqlite;

namespace Kado.Data.Migrations;

/// <summary>
/// スキーマの版を進める。
/// <para>
/// 版の記録には SQLite 組み込みの <c>PRAGMA user_version</c> を使う。専用のテーブルを
/// 作るより単純で、テーブルが壊れた場合の扱いを考えずに済む。
/// </para>
/// <para>
/// 各版は<b>ひとつのトランザクション</b>で適用する。途中で失敗しても中途半端な
/// スキーマが残らないようにするため。
/// </para>
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>現在のスキーマ版。まだ何も適用していなければ 0。</summary>
    public static int GetVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteScalar<int>("PRAGMA user_version;");
    }

    /// <summary>
    /// 最新の版まで進める。適用済みの版は飛ばすので、何度呼んでも構わない。
    /// </summary>
    /// <returns>この呼び出しで適用した版（適用するものが無ければ空）。</returns>
    public static IReadOnlyList<Migration> Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = GetVersion(connection);

        // 将来の版で作られたデータベースは、このコードでは正しく読めない。
        // 黙って動かすと壊れたデータを書き込みかねないので、適用するものが無い場合も含めて先に止める。
        if (current > SchemaMigrations.LatestVersion)
        {
            throw new InvalidOperationException(
                $"データベースのスキーマ版 {current} は、このアプリが知っている最新版 "
                + $"{SchemaMigrations.LatestVersion} より新しいため開けません。"
                + "新しいバージョンのアプリで開いてください。");
        }

        var pending = SchemaMigrations.All
            .Where(m => m.Version > current)
            .OrderBy(m => m.Version)
            .ToArray();

        if (pending.Length == 0) return [];

        foreach (var migration in pending)
        {
            using var transaction = connection.BeginTransaction();

            connection.Execute(migration.Sql, transaction: transaction);

            // PRAGMA はパラメータを受け付けないので値を埋め込む。
            // Version は int なので、埋め込んでも注入の余地はない。
            connection.Execute($"PRAGMA user_version = {migration.Version};", transaction: transaction);

            transaction.Commit();
        }

        return pending;
    }
}
