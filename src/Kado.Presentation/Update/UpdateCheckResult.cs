namespace Kado.Presentation.Update;

/// <summary>いま確かめている状態。押し直したときに「最新です」と誤って言わないための4状態。</summary>
public enum UpdateCheckStatus
{
    /// <summary>いま別の確認が進んでいて、始められなかった。</summary>
    AlreadyChecking,

    /// <summary>確かめられなかった（通信できない、応答を読めないなど）。理由は <c>Failure</c> に入る。</summary>
    Failed,

    /// <summary>最新版を使っている。</summary>
    UpToDate,

    /// <summary>新しい版がある。</summary>
    UpdateAvailable,
}

/// <summary>
/// 更新の確認の結果。
/// <para>
/// 以前は「新しい版があれば <see cref="UpdateInfo"/>、無ければ <c>null</c>」だけを返していたが、
/// これだと「確認中で始められなかった」ときも <c>null</c> になり、呼び出し側が「最新です」と
/// 誤って伝えてしまっていた。状態を4つに分けて区別できるようにする。
/// </para>
/// </summary>
/// <param name="Status">いまの状態。</param>
/// <param name="Info"><see cref="UpdateCheckStatus.UpdateAvailable"/> のときだけ入る。</param>
/// <param name="Failure"><see cref="UpdateCheckStatus.Failed"/> のときの理由。</param>
public readonly record struct UpdateCheckResult(
    UpdateCheckStatus Status, UpdateInfo? Info = null, UpdateFailure? Failure = null)
{
    public static UpdateCheckResult AlreadyChecking() => new(UpdateCheckStatus.AlreadyChecking);

    public static UpdateCheckResult Failed(UpdateFailure failure) =>
        new(UpdateCheckStatus.Failed, Failure: failure);

    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);

    public static UpdateCheckResult Available(UpdateInfo info) => new(UpdateCheckStatus.UpdateAvailable, info);
}
