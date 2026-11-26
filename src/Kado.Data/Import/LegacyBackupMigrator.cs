using Microsoft.Data.Sqlite;
using Kado.Core.Import;
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

    /// <summary>
    /// 読み込み済みの内容を書き込む。
    /// <para>予定・タスク・実働日・設定・同期状態の消去は、<b>1つのトランザクション</b>で行う。途中で失敗したら、どれも反映しない。</para>
    /// </summary>
    public void Apply(LegacyImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // 実働日は既存と併合する。バックアップの期間だけを差し替え、期間外の既存データと
        // マイルストーンは残す（全面的な置き換えにしない）。読むのは書き始める前に済ませる
        var workingDays = new WorkingDayRepository(_connection);
        var merged = result.WorkingDays.Count > 0
            ? workingDays.Load().Merge(ToImportResult(result))
            : null;

        using var transaction = _connection.BeginTransaction();

        new EventRepository(_connection).UpsertMany(result.Events, transaction);
        new TaskRepository(_connection).UpsertMany(result.Tasks, transaction);

        if (merged is not null) workingDays.Save(merged, transaction);

        var settings = new SettingsRepository(_connection);
        if (result.Settings.Count > 0) settings.SetMany(result.Settings, transaction);

        // 同期トークンは引き継げないので、必ず空にしてから始める。
        // 古いトークンが残っていると差分同期が噛み合わず、取りこぼしが出る
        settings.ClearSyncState(transaction);

        transaction.Commit();
    }

    /// <summary>旧バックアップの実働日を、<see cref="WorkingDayCalendar.Merge"/> に渡せる形にする。</summary>
    private static ImportResult ToImportResult(LegacyImportResult result) =>
        new(
            Version: string.Empty,
            WorkingDayRangeStart: result.WorkingDayRangeStart ?? result.WorkingDays.Min(),
            WorkingDayRangeEnd: result.WorkingDayRangeEnd ?? result.WorkingDays.Max(),
            MilestoneRangeStart: null,
            MilestoneRangeEnd: null,
            WorkingDays: result.WorkingDays,
            Milestones: [],
            Warnings: []);
}
