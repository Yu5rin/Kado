namespace Kado.Presentation.Update;

/// <summary>見つけた新しい版を、自動で落としてよいか。落とさないときの理由も持つ。</summary>
public enum AutoDownloadDecision
{
    /// <summary>落としてよい。</summary>
    Yes,

    /// <summary>設定で「自動で更新する」を切っている。</summary>
    Disabled,

    /// <summary>見つけた版が、いまの版以下。</summary>
    NotNewer,

    /// <summary>
    /// SHA256 が分からない（API が上限で断られ、配布物の URL を組み立てた経路）。
    /// 照合できないものを黙って実行しないため、自動では入れ替えない。
    /// </summary>
    NoSha256,

    /// <summary>この版の自動の入れ替えは、前に失敗して止めてある。</summary>
    Blocked,

    /// <summary>この版（かそれより新しい版）はもう控えてある。</summary>
    AlreadyStaged,

    /// <summary>いまの exe のフォルダに書けない。自分では入れ替えられない。</summary>
    CannotWriteInstallDirectory,
}

/// <summary>入れ替えを待たせている理由。<see cref="None"/> なら入れ替えてよい。</summary>
public enum AutoUpdateWait
{
    None,
    Disabled,
    NothingStaged,
    Busy,
    OtherWindowOpen,
    ModalDialogOpen,
    MenuOrDragActive,
    SyncRunning,
    IdleUnknown,
    NotIdleLongEnough,
}

/// <summary>入れ替えてよいかを決めるときに見る、いまの状態。</summary>
/// <param name="Enabled">設定で「自動で更新する」が入っているか。</param>
/// <param name="HasStaged">入れ替え待ちがあるか。</param>
/// <param name="Busy">更新そのものの最中か（更新の窓が仕事中・落としている・入れ替えている・終わりかけ）。</param>
/// <param name="OtherWindowOpen">メインウィンドウ以外の窓（編集画面・設定・更新の窓・確認の窓など）が開いているか。</param>
/// <param name="ModalDialogOpen">メインウィンドウが押せない状態か（メッセージボックスなどの最中）。</param>
/// <param name="MenuOrPopupOpen">右クリックのメニューやポップアップが開いているか。</param>
/// <param name="DragInProgress">ドラッグの最中か。</param>
/// <param name="SyncRunning">Google との同期の最中か。</param>
/// <param name="IdleFor">キーボードとマウスの操作が無い時間。分からなければ null。</param>
public readonly record struct AutoUpdateState(
    bool Enabled,
    bool HasStaged,
    bool Busy,
    bool OtherWindowOpen,
    bool ModalDialogOpen,
    bool MenuOrPopupOpen,
    bool DragInProgress,
    bool SyncRunning,
    TimeSpan? IdleFor);

/// <summary>
/// 自動更新の判断。通信も画面も時計も持たない純粋な関数にして、判断の中身を試せるようにしてある
/// （操作の無い時間は、呼ぶ側が測って渡す）。
/// </summary>
public static class AutoUpdatePolicy
{
    /// <summary>この時間、キーボードとマウスの操作が無ければ、手が空いているとみなす。</summary>
    public static readonly TimeSpan RequiredIdle = TimeSpan.FromMinutes(10);

    /// <summary>入れ替えてよいかを見回る間隔。</summary>
    public static readonly TimeSpan PatrolInterval = TimeSpan.FromMinutes(1);

    /// <summary>見つけた版を、自動で落としてよいか。</summary>
    /// <param name="enabled">設定で「自動で更新する」が入っているか。</param>
    /// <param name="info">見つけた版。</param>
    /// <param name="current">いま動いている版。</param>
    /// <param name="staged">すでにある控え。</param>
    /// <param name="blockedTag">自動の入れ替えを止めてあるタグ（前に入れ替えに失敗した版）。</param>
    /// <param name="canWriteInstallDirectory">いまの exe のフォルダに書けるか。</param>
    public static AutoDownloadDecision DecideDownload(
        bool enabled, UpdateInfo info, Version current, StagedUpdate? staged,
        string? blockedTag, bool canWriteInstallDirectory)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(current);

        if (!enabled) return AutoDownloadDecision.Disabled;

        if (!ReleaseFeed.IsNewerThan(info, current)) return AutoDownloadDecision.NotNewer;

        // ハッシュが取れていない版は、自動では入れ替えない
        if (info.DetailsUnavailable || string.IsNullOrEmpty(info.Sha256)) return AutoDownloadDecision.NoSha256;

        if (blockedTag is { Length: > 0 } &&
            string.Equals(blockedTag, info.TagName, StringComparison.OrdinalIgnoreCase))
        {
            return AutoDownloadDecision.Blocked;
        }

        if (staged?.Version is { } held && held >= info.Version) return AutoDownloadDecision.AlreadyStaged;

        if (!canWriteInstallDirectory) return AutoDownloadDecision.CannotWriteInstallDirectory;

        return AutoDownloadDecision.Yes;
    }

    /// <summary>落とさない理由を、shell.log に書く言葉にする。</summary>
    public static string Describe(AutoDownloadDecision decision) => decision switch
    {
        AutoDownloadDecision.Yes => "自動で落とす",
        AutoDownloadDecision.Disabled => "設定で自動更新を切っているため落とさない",
        AutoDownloadDecision.NotNewer => "いまの版以下のため落とさない",
        AutoDownloadDecision.NoSha256 => "SHA256を取れていないため自動では入れ替えない（通知だけにする）",
        AutoDownloadDecision.Blocked => "この版は前の入れ替えに失敗して止めてあるため落とさない",
        AutoDownloadDecision.AlreadyStaged => "この版はもう控えてある",
        AutoDownloadDecision.CannotWriteInstallDirectory => "exeのフォルダに書けないため自動では入れ替えない",
        _ => decision.ToString(),
    };

    /// <summary>いま入れ替えてよいか。</summary>
    public static bool CanApplyNow(AutoUpdateState state) => Wait(state) == AutoUpdateWait.None;

    /// <summary>入れ替えを待たせている理由。入れ替えてよいなら <see cref="AutoUpdateWait.None"/>。</summary>
    public static AutoUpdateWait Wait(AutoUpdateState state)
    {
        if (!state.Enabled) return AutoUpdateWait.Disabled;
        if (!state.HasStaged) return AutoUpdateWait.NothingStaged;
        if (state.Busy) return AutoUpdateWait.Busy;
        if (state.OtherWindowOpen) return AutoUpdateWait.OtherWindowOpen;
        if (state.ModalDialogOpen) return AutoUpdateWait.ModalDialogOpen;
        if (state.MenuOrPopupOpen || state.DragInProgress) return AutoUpdateWait.MenuOrDragActive;
        if (state.SyncRunning) return AutoUpdateWait.SyncRunning;

        // 操作の無い時間が分からないときは、手が空いているとは言えない
        if (state.IdleFor is not { } idle) return AutoUpdateWait.IdleUnknown;

        return idle >= RequiredIdle ? AutoUpdateWait.None : AutoUpdateWait.NotIdleLongEnough;
    }

    /// <summary>待っている理由を、shell.log に書く言葉にする。</summary>
    public static string Describe(AutoUpdateWait wait) => wait switch
    {
        AutoUpdateWait.None => "入れ替えてよい",
        AutoUpdateWait.Disabled => "設定で自動更新を切っている",
        AutoUpdateWait.NothingStaged => "入れ替え待ちが無い",
        AutoUpdateWait.Busy => "更新の処理の最中",
        AutoUpdateWait.OtherWindowOpen => "メインウィンドウ以外の窓が開いている",
        AutoUpdateWait.ModalDialogOpen => "確認の窓などが開いている",
        AutoUpdateWait.MenuOrDragActive => "メニューやポップアップ、ドラッグの最中",
        AutoUpdateWait.SyncRunning => "同期の最中",
        AutoUpdateWait.IdleUnknown => "操作の無い時間が分からない",
        AutoUpdateWait.NotIdleLongEnough => "操作の無い時間が10分に届かない",
        _ => wait.ToString(),
    };
}
