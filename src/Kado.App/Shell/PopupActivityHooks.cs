using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Kado.App.Shell;

/// <summary>
/// アプリ全体の <c>ContextMenu</c>・<c>Popup</c> の開閉を、まとめて
/// <see cref="Tracker"/> へ数える（項目3）。
/// <para>
/// 狭い幅で出しているスライドは、メニューやポップアップが窓の右端からはみ出す。
/// カーソルをその上（＝窓の外）へ動かすと、ホットゾーンが「窓から外れた」と
/// 判定してスライドが引っ込み、開いていたメニューも一緒に消えて選べなかった
/// （実機の不具合）。<see cref="ShellController.SlideOutIfIdle"/> はここを見て、
/// 自分が出したメニュー・ポップアップが開いているあいだは引っ込めない。
/// </para>
/// <para>
/// <b>Themes/Controls.xaml の暗黙スタイルに EventSetter を足す案は採らなかった。</b>
/// EventSetter.Handler は、その XAML ファイルにコンパイル済みの x:Class（コード
/// ビハインド）が無いと解決できない。Controls.xaml は素の ResourceDictionary
/// （x:Class 無し）なので使えない。代わりに、型だけを見て拾える
/// <see cref="EventManager.RegisterClassHandler(Type, RoutedEvent, Delegate)"/>
/// で一括登録する。ContextMenu.Opened／Closed は RoutedEvent なのでそのまま
/// 拾えるが、<see cref="Popup"/> の Opened／Closed は素の CLR イベント（RoutedEvent
/// ではない）なので同じ手が使えない。代わりに Popup 自身の Loaded
/// （FrameworkElement.LoadedEvent。これは RoutedEvent）をクラスハンドラで拾い、
/// そこで初めて Opened／Closed をインスタンスごとに配線する。ComboBox・
/// DatePicker の暦・検索結果のポップアップは、置き場所を問わずすべてこの
/// <see cref="Popup"/> 型を使っているので、これで一括して拾える。
/// </para>
/// </summary>
internal static class PopupActivityHooks
{
    /// <summary>いま開いているメニュー・ポップアップの数。</summary>
    internal static readonly PopupActivityTracker Tracker = new();

    /// <summary>
    /// Loaded で配線した Popup を覚えておき、二重に配線しない。
    /// <para>
    /// Popup はテンプレートの再適用などで Loaded が複数回来ることがある。
    /// キーを弱参照で持つので、Popup が消えても覚え続けてリークしない。
    /// </para>
    /// </summary>
    private static readonly ConditionalWeakTable<Popup, object> _wiredPopups = new();

    private static bool _installed;

    /// <summary>
    /// アプリ起動時に1回呼ぶ（<c>App.OnStartup</c>）。
    /// <para>何度呼んでも安全（2回目以降は何もしない）。</para>
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((_, _) => Tracker.Opened()));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
            new RoutedEventHandler((_, _) => Tracker.Closed()));

        EventManager.RegisterClassHandler(typeof(Popup), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPopupLoaded));
    }

    private static void OnPopupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Popup popup) return;
        if (_wiredPopups.TryGetValue(popup, out _)) return;

        _wiredPopups.Add(popup, popup);
        popup.Opened += (_, _) => Tracker.Opened();
        popup.Closed += (_, _) => Tracker.Closed();
    }
}
