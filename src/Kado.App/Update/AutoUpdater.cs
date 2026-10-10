using System.IO;
using Kado.Presentation.Settings;
using Kado.Presentation.Update;

namespace Kado.App.Update;

/// <summary>
/// 自動更新の段取り。<b>落とす・SHA256 を照合する・入れ替える・失敗したら戻す・再起動する</b>部分は、
/// 手で更新するときと同じ <see cref="UpdateService"/> と <see cref="ExecutableSwap"/> をそのまま使う
/// （ここでは二重に書かない）。ここが持つのは、入れ替え待ちの控え（設定）と、各段階の記録だけ。
/// <para>
/// 設定は画面用の接続で読み書きするので、<b>画面のスレッドから呼ぶ</b>こと。
/// </para>
/// </summary>
internal sealed class AutoUpdater
{
    private readonly UpdateService _updater;
    private readonly AppSettings _settings;
    private readonly Action<string> _log;

    public AutoUpdater(UpdateService updater, AppSettings settings, Action<string> log)
    {
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>入れ替え待ち。無ければ null。</summary>
    public StagedUpdate? Staged => _settings.StagedUpdate;

    /// <summary>新しい版を見つけたとき、自動で落としてよいか。</summary>
    public AutoDownloadDecision Decide(UpdateInfo info) =>
        AutoUpdatePolicy.DecideDownload(
            _settings.AutoUpdate, info, UpdateService.CurrentVersion, Staged,
            _settings.AutoUpdateBlockedTag, UpdateService.CanWriteToInstallDirectory(out _));

    /// <summary>
    /// 見つけた版を裏で落として、SHA256 を照合し、通ったものだけ入れ替え待ちとして控える。
    /// <para>
    /// 落とす・照合するのは <see cref="UpdateService.DownloadAsync"/>（合わなければ消して例外にする）。
    /// 失敗したら記録して false を返す（呼んだ側が、今までどおりの通知に戻す。次の確認でまた試す）。
    /// </para>
    /// </summary>
    /// <returns>入れ替え待ちとして控えられたか。</returns>
    public async Task<bool> TryStageAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        var decision = Decide(info);

        if (decision != AutoDownloadDecision.Yes)
        {
            _log($"自動更新: {info.TagName} は落とさない。{AutoUpdatePolicy.Describe(decision)}");
            return decision == AutoDownloadDecision.AlreadyStaged;
        }

        _log($"自動更新: 新しい版 {info.TagName} を見つけた。裏で落とす");

        try
        {
            // 画面のスレッドへ戻ってくる（設定は画面用の接続で書く）
            var path = await _updater.DownloadAsync(
                info, progress: null, cancellationToken, UpdateService.StagedDirectory).ConfigureAwait(true);

            // DownloadAsync が SHA256 を照合して、合わなければ例外にしている。ここへ来たものは照合済み。
            // Decide が「ハッシュあり」を確かめているので、照合を省いて通ったものは無い
            _log($"自動更新: {info.TagName} を落として照合した（SHA256 一致）");

            var previous = Staged;
            _settings.StagedUpdate = StagedUpdate.From(info, path);

            _log($"自動更新: {info.TagName} を入れ替え待ちにした（手が空いたときに入れ替える）");

            // 古い控えが別のファイルなら、もう要らない
            if (previous is not null &&
                !string.Equals(previous.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                UpdateService.TryDelete(previous.FilePath);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _log($"自動更新: {info.TagName} の取得を中止した");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 詳しい理由は DownloadAsync が1行残している。ここでは、通知に戻すことだけ記す
            _log($"自動更新: {info.TagName} を落とせなかったので、通知だけにする。{UpdateDiagnostics.Summarize(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 控えが、いま使ってよいものか確かめる（ハッシュの計算を含む。70MB なので画面のスレッドでは呼ばない）。
    /// </summary>
    public StagedVerdict Verify(StagedUpdate staged) =>
        StagedUpdateCheck.Evaluate(
            staged, UpdateService.CurrentVersion, UpdateService.StagedDirectory,
            File.Exists, UpdateService.ComputeSha256);

    /// <summary>
    /// 見つけた版と同じ版の、使える控えを返す（手で更新するときに、落とし直さないため）。無ければ null。
    /// 使えない控えは、理由を記録して捨てる。
    /// </summary>
    public async Task<StagedUpdate?> FindUsableAsync(UpdateInfo info)
    {
        if (Staged is not { } staged || staged.Version != info.Version) return null;

        var verdict = await Task.Run(() => Verify(staged)).ConfigureAwait(true);

        if (verdict == StagedVerdict.Usable) return staged;

        Discard(staged, StagedUpdateCheck.Describe(verdict));
        return null;
    }

    /// <summary>控えを捨てる（ファイルと設定）。理由を記録する。</summary>
    public void Discard(StagedUpdate staged, string reason)
    {
        _log($"自動更新: {staged.Tag} の控えを捨てる。{reason}");

        UpdateService.TryDelete(staged.FilePath);

        if (Staged is { } now && string.Equals(now.Tag, staged.Tag, StringComparison.OrdinalIgnoreCase))
        {
            _settings.StagedUpdate = null;
        }
    }

    /// <summary>
    /// 入れ替える直前の記録。<b>入れ替えたあとの最初の起動</b>が「更新しました」と知らせるための控え。
    /// 入れ替えに失敗したら <see cref="ClearApplied"/> で消す。
    /// </summary>
    public void RecordApplied(StagedUpdate applied) => _settings.AppliedUpdate = applied;

    /// <summary>入れ替えに失敗したので、「更新しました」の控えを消す。</summary>
    public void ClearApplied() => _settings.AppliedUpdate = null;

    /// <summary>
    /// 控えた版へ入れ替えて、新しいほうを起動する（<see cref="UpdateService.Apply"/>。失敗したら元の版へ戻る）。
    /// 成功したら、呼んだ側はすぐアプリを終わらせること。
    /// <para>
    /// 入れ替えに失敗したら、この版の自動の入れ替えは止める（毎日同じ失敗を繰り返さない）。
    /// 手で更新するのは、これまでどおり使える。
    /// </para>
    /// </summary>
    /// <param name="staged">控え（<see cref="Verify"/> を通ったもの）。</param>
    /// <param name="keepHidden">新しいほうに、窓を隠したまま起動するよう伝えるか。</param>
    public async Task<SwapResult> ApplyAsync(StagedUpdate staged, bool keepHidden)
    {
        RecordApplied(staged);
        _log($"自動更新: {staged.Tag} に入れ替える");

        var result = await _updater.ApplyAsync(staged.FilePath, keepHidden).ConfigureAwait(true);

        return Finish(staged, result);
    }

    /// <summary>
    /// <see cref="ApplyAsync"/> の同期版。起動の最初（窓を出す前）に、画面のスレッドのまま入れ替える。
    /// </summary>
    public SwapResult ApplySync(StagedUpdate staged)
    {
        RecordApplied(staged);
        _log($"自動更新: {staged.Tag} に入れ替える");

        return Finish(staged, _updater.Apply(staged.FilePath, keepHidden: false));
    }

    private SwapResult Finish(StagedUpdate staged, SwapResult result)
    {
        if (result.Succeeded)
        {
            _settings.StagedUpdate = null;
            _log($"自動更新: {staged.Tag} に入れ替えた。再起動する");
            return result;
        }

        ClearApplied();
        _settings.AutoUpdateBlockedTag = staged.Tag;
        UpdateService.TryDelete(staged.FilePath);
        _settings.StagedUpdate = null;

        _log($"自動更新: {staged.Tag} への入れ替えをやめた（{result.Outcome}）。この版は自動では入れ替えない");
        return result;
    }

    /// <summary>
    /// 起動の最初に、前の起動までの控えを片付け、入れ替えるべきものがあれば返す。
    /// <list type="bullet">
    /// <item>控えの版が、いまの版以下（入れ替えが済んだ・古い）→ 捨てる</item>
    /// <item>ファイルが無い・ハッシュが合わない・置き場所の外 → 捨てる</item>
    /// <item>設定で自動更新を切っている → 捨てる（手で更新するときは落とし直す）</item>
    /// </list>
    /// </summary>
    /// <returns>起動の最初に入れ替えるべき控え。無ければ null。</returns>
    public StagedUpdate? TakeStagedForStartup()
    {
        if (Staged is not { } staged)
        {
            _updater.CleanupStaleStagedFiles(keep: null);
            return null;
        }

        if (!_settings.AutoUpdate)
        {
            Discard(staged, "設定で自動更新を切っている");
            _updater.CleanupStaleStagedFiles(keep: null);
            return null;
        }

        var verdict = Verify(staged);

        if (verdict != StagedVerdict.Usable)
        {
            Discard(staged, StagedUpdateCheck.Describe(verdict));
            _updater.CleanupStaleStagedFiles(keep: null);
            return null;
        }

        _updater.CleanupStaleStagedFiles(staged.FilePath);
        _log($"自動更新: 入れ替え待ちの {staged.Tag} がある。起動の最初に入れ替える");
        return staged;
    }

    /// <summary>
    /// 入れ替えたあとの最初の起動で、「更新しました」と知らせる版を返す。<b>一度だけ</b>（読んだら控えを消す）。
    /// 控えの版がいまの版と違うとき（入れ替えが成り立たなかった）は、知らせずに捨てる。
    /// </summary>
    public StagedUpdate? TakeAppliedNotice()
    {
        if (_settings.AppliedUpdate is not { } applied) return null;

        _settings.AppliedUpdate = null;

        if (applied.Version != UpdateService.CurrentVersion)
        {
            _log($"自動更新: 更新の知らせは出さない（控えの版 {applied.Tag} といまの版 {UpdateService.CurrentVersion} が違う）");
            return null;
        }

        _log($"自動更新: {applied.Tag} に更新できた");
        return applied;
    }
}
