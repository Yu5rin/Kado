using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AppBarProbe;

/// <summary>
/// AppBar プロトタイプのエントリ。
/// <para>
/// ここには<b>安全装置3点</b>（要件書 2.3）だけを置いてある。AppBar の本体は
/// <see cref="AppBarController"/>。
/// </para>
/// <list type="number">
///   <item><c>AppDomain.UnhandledException</c> と <c>DispatcherUnhandledException</c> の両方で解除</item>
///   <item>ミューテックスで二重起動を防止（AppBar の多重登録を防ぐ）</item>
///   <item>起動時に前回の異常終了を検知し、<c>SPI_SETWORKAREA</c> でワークエリアを復旧</item>
/// </list>
/// </summary>
public partial class App : Application
{
    /// <summary>二重起動の判定に使う名前。セッション内で一意であればよい。</summary>
    private const string MutexName = "SlideinaCalendar.AppBarProbe.SingleInstance";

    private Mutex? _mutex;

    /// <summary>緊急解除のために、生成済みのコントローラを保持する。</summary>
    internal static AppBarController? ActiveController { get; set; }

    /// <summary>起動時の復旧結果。MainWindow が読んでログに出す。</summary>
    internal static string? RecoveryMessage { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // --- 安全装置2: 二重起動の防止 ---
        // AppBar を2つ登録すると、片方を解除してももう片方が残り、
        // 解除漏れの原因になる。起動の時点で止める。
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "AppBarProbe はすでに起動しています。\nAppBar の多重登録を防ぐため、2つ目は起動しません。",
                "AppBarProbe", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // --- 安全装置1: どの経路で落ちても AppBar を解除する ---
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ActiveController?.EmergencyUnregister();

        // --- 安全装置3: 前回の異常終了からの復旧 ---
        // ABM_REMOVE を呼ばずに落ちた場合、Windows はワークエリアを削ったまま放置する。
        RecoveryMessage = WorkAreaRecovery.RecoverIfNeeded();

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ActiveController?.EmergencyUnregister();

        MessageBox.Show(
            $"エラーが発生したため AppBar を解除しました。\nワークエリアは元に戻っています。\n\n{e.Exception.Message}",
            "AppBarProbe", MessageBoxButton.OK, MessageBoxImage.Error);

        // 解除さえ済めば続行して構わない。復旧できたことを画面で確認させる。
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // UI スレッド以外で落ちた場合。ここは戻ってこられないので解除だけ行う。
        ActiveController?.EmergencyUnregister();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ActiveController?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
