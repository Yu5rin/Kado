namespace Kado.Presentation.Notifications;

/// <summary>
/// 画面の外に知らせる口。
/// <para>
/// Windows の通知を出すのは OS の作法に触れるので、実装は App 側に置く。
/// ここを通しておけばテストでは何も出さない実装に差し替えられる。
/// </para>
/// </summary>
public interface INotifier
{
    /// <summary>知らせられる環境か。出せないなら設定に出さない。</summary>
    bool IsSupported { get; }

    /// <summary>知らせる。</summary>
    /// <param name="title">見出し。</param>
    /// <param name="message">本文。複数行になることがある。</param>
    /// <param name="withSound">音を鳴らすか。</param>
    void Notify(string title, string message, bool withSound = true);
}

/// <summary>何も出さない実装。テストと、通知を扱えない環境で使う。</summary>
public sealed class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();

    private NullNotifier() { }

    public bool IsSupported => false;

    public void Notify(string title, string message, bool withSound = true) { }
}
