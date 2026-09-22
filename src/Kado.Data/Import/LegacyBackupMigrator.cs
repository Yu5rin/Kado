using Microsoft.Data.Sqlite;
using Kado.Core.WorkingDays;
using Kado.Data.Repositories;

namespace Kado.Data.Import;

/// <summary>
/// 旧バックアップを読み込んでデータベースへ書き込む。
/// <para>
/// 読み込み（<see cref="LegacyBackupImporter"/>）と保存を分けてあるのは、取り込み内容を
/// 先に確認してから書き込むという流れを取れるようにするため。移行はやり直しが効かない。
/// </para>
/// </summary>
public sealed class LegacyBackupMigrator(SqliteConnection connection)
{
    private readonly SqliteConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>読み込むだけ。データベースには触らない。</summary>
    public static LegacyImportResult Read(Stream json) => new LegacyBackupImporter().Import(json);

    /// <summary>読み込んで書き込む。</summary>
    public LegacyImportResult Migrate(Stream json)
    {
        var result = Read(json);
        Apply(result);
        return result;
    }

    /// <summary>読み込み済みの内容を書き込む。</summary>
    public void Apply(LegacyImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        new EventRepository(_connection).UpsertMany(result.Events);
        new TaskRepository(_connection).UpsertMany(result.Tasks);

        if (result.WorkingDays.Count > 0)
        {
            // 実働日は既存と併合する。バックアップには無いマイルストーンを消さないため、
            // 全面的な置き換えではなく Merge を通す
            var repository = new WorkingDayRepository(_connection);
            var existing = repository.Load();

            var incoming = WorkingDayCalendar.Create(
                result.WorkingDays,
                result.WorkingDayRangeStart, result.WorkingDayRangeEnd,
                existing.AllMilestones, existing.MilestoneRangeStart, existing.MilestoneRangeEnd);

            repository.Save(incoming);
        }

        var settings = new SettingsRepository(_connection);
        if (result.Settings.Count > 0) settings.SetMany(result.Settings);

        // 同期トークンは引き継げないので、必ず空にしてから始める。
        // 古いトークンが残っていると差分同期が噛み合わず、取りこぼしが出る
        settings.ClearSyncState();
    }
}
