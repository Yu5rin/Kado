using System.Windows;
using Kado.App.Views;
using Kado.Presentation.Notifications;

namespace Kado.App.Notifications;

/// <summary>
/// 画面の隅に小窓で知らせる。
/// <para>
/// 非 MSIX の配布で Windows のトーストを出すには AUMID の登録と COM
/// アクティベーターが要る（要件書 7.5）。そこまでせずとも用は足りるので、
/// 同じ配色の小窓を自前で出す。
/// </para>
/// </summary>
public sealed class ToastNotifier : INotifier
{
    public bool IsSupported => Application.Current is not null;

    public void Notify(string title, string message, bool withSound = true)
    {
        if (Application.Current is not { Dispatcher: { } dispatcher }) return;

        // 時計は画面の筋から来るが、念のため画面の筋に戻してから出す
        dispatcher.Invoke(() => ToastWindow.Show(title, message, withSound));
    }
}
