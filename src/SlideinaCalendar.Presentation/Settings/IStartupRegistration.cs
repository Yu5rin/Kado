namespace SlideinaCalendar.Presentation.Settings;

/// <summary>
/// Windows にログオンしたときの自動起動。
/// <para>
/// レジストリを触るので、実装は App 側に置く。ここを通しておけばテストでは
/// 何もしない実装に差し替えられる。
/// </para>
/// </summary>
public interface IStartupRegistration
{
    /// <summary>この環境で扱えるか。扱えないときは設定画面に出さない。</summary>
    bool IsSupported { get; }

    /// <summary>いま登録されているか。</summary>
    bool IsEnabled { get; }

    /// <summary>登録する、または外す。</summary>
    /// <returns>できたら true。書き込めなければ false。</returns>
    bool SetEnabled(bool enabled);
}

/// <summary>何もしない実装。テストと、自動起動を扱えない環境で使う。</summary>
public sealed class NullStartupRegistration : IStartupRegistration
{
    public static readonly NullStartupRegistration Instance = new();

    private NullStartupRegistration() { }

    public bool IsSupported => false;

    public bool IsEnabled => false;

    public bool SetEnabled(bool enabled) => false;
}
