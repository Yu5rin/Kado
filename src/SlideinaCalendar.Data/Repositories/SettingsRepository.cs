using Dapper;
using Microsoft.Data.Sqlite;

namespace SlideinaCalendar.Data.Repositories;

/// <summary>
/// 設定と同期状態の読み書き。
/// <para>
/// どちらもキーと値だけの単純な表にしてある。設定は項目の増減が多く、そのたびに
/// スキーマを変えたくないため。構造のある値は JSON にして入れる。
/// </para>
/// <para>
/// 同期状態（syncToken など）を設定と分けているのは、バックアップや初期化の扱いが
/// 違うため。設定は残したいが同期状態は捨てたい、という場面がある。
/// </para>
/// </summary>
public sealed class SettingsRepository(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>設定を読む。無ければ null。</summary>
    public string? Get(string key) =>
        _connection.QuerySingleOrDefault<string>("SELECT value FROM settings WHERE key = @key;", new { key });

    /// <summary>設定を読む。無ければ既定値。</summary>
    public string GetOrDefault(string key, string fallback) => Get(key) ?? fallback;

    /// <summary>設定を書く。</summary>
    public void Set(string key, string value, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """,
            new { key, value }, transaction);
    }

    /// <summary>まとめて書く。</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var transaction = _connection.BeginTransaction();
        foreach (var (key, value) in values) Set(key, value, transaction);
        transaction.Commit();
    }

    /// <summary>設定を全件読む。</summary>
    public IReadOnlyDictionary<string, string> All() =>
        _connection.Query<(string Key, string Value)>("SELECT key, value FROM settings;")
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    /// <summary>設定を消す。</summary>
    public bool Remove(string key) =>
        _connection.Execute("DELETE FROM settings WHERE key = @key;", new { key }) > 0;

    // ------------------------------------------------------------------
    // 同期状態
    // ------------------------------------------------------------------

    /// <summary>同期状態を読む。無ければ null。</summary>
    public string? GetSyncState(string key) =>
        _connection.QuerySingleOrDefault<string>("SELECT value FROM sync_state WHERE key = @key;", new { key });

    /// <summary>同期状態を書く。</summary>
    public void SetSyncState(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _connection.Execute(
            """
            INSERT INTO sync_state (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """,
            new { key, value });
    }

    /// <summary>
    /// 同期状態をすべて消す。全再同期をかけるときに使う。
    /// <para>旧データからの移行では syncToken を引き継げないため、初回はここを空にして始める。</para>
    /// </summary>
    public void ClearSyncState() => _connection.Execute("DELETE FROM sync_state;");
}
