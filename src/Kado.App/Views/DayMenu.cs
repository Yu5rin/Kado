using System.Windows;

namespace Kado.App.Views;

/// <summary>
/// 日付・空き時間の右クリックメニューに渡す、右クリックした場所の時刻。
/// <para>
/// 時間軸の列（<see cref="TimelineColumnView"/>）だけが付ける。「この日に予定を追加」は、時間帯の上で
/// 右クリックしたとき、その時刻（15分に丸めた）から1時間の予定にする。メニューは
/// <c>PlacementTarget</c> のこの値を読む（<c>DayMenuInfoConverter</c>）。ほかの場所では付かない（null）。
/// </para>
/// <para>
/// メニューが開く<b>前</b>に付ける必要がある。右ボタンを押した時点（メニューは離したときに開く）で付ける。
/// </para>
/// </summary>
public static class DayMenu
{
    public static readonly DependencyProperty TimeProperty = DependencyProperty.RegisterAttached(
        "Time", typeof(TimeOnly?), typeof(DayMenu), new PropertyMetadata(null));

    public static TimeOnly? GetTime(DependencyObject element) => (TimeOnly?)element.GetValue(TimeProperty);

    public static void SetTime(DependencyObject element, TimeOnly? value) => element.SetValue(TimeProperty, value);
}
