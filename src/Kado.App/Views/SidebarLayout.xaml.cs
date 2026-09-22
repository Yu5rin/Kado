using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kado.Presentation.ViewModels;

namespace Kado.App.Views;

/// <summary>
/// 右パネルの中身。
/// <para>
/// 月の見出し行＋折りたためる月カレンダー＋仕切り＋<see cref="DayPaneView"/>
/// （選んだ日の予定・タスク）を縦に並べる。旧版では「スリムパネル」という
/// 独立したパネルだったが、右パネルへ統合し、パネルは 左・中央・右 の3つになった
/// （<c>MainWindow.xaml</c> の Grid.Column=6 に置く）。
/// </para>
/// <para>データの出どころは3ペインと同じ <c>MainViewModel</c>。形だけを変えている。</para>
/// </summary>
public partial class SidebarLayout : UserControl
{
    public SidebarLayout()
    {
        InitializeComponent();

        // 仕切りの位置は覚えておく。カレンダーを広く見たい人と、予定の一覧を
        // 長く出したい人がいる。畳んだ状態（項目2）も、開いたときの高さが
        // 噛み合うよう同じ場所（ApplyCollapsedState）で決める
        Loaded += (_, _) => ApplyCollapsedState();
        Split.DragCompleted += (_, _) => SaveShare();
    }

    /// <summary>
    /// 控えてある割り振りに戻す（項目5）。
    /// <para>
    /// 一度もつまんで変えていない（<see cref="MainViewModel.HasPaneCalendarShare"/>
    /// が false の）あいだは <c>CalendarRow</c> を <c>Height="Auto"</c> に戻す。
    /// 月カレンダーが必要とする高さ（曜日の見出し＋6週ぶん。MonthView.xaml.cs の
    /// FitCells が MinHeight に持つ）が、そのまま初期値になる。固定の割合だと
    /// 画面の高さが変わるたびに「余白が余る」「6週目が切れる」が起きるが、
    /// Auto はその心配が無い。
    /// </para>
    /// <para>
    /// つまんで変えたあと（SaveShare が呼ばれたあと）は、控えてある割合を
    /// Star 比で組み直す。以前からの挙動と同じ。
    /// </para>
    /// <para>
    /// <see cref="ApplyCollapsedState"/> が畳んだとき Height／MinHeight を 0 に
    /// 書き換えるので、開いたときはここで両方とも書き戻す（項目2）。以前は
    /// <c>HasPaneCalendarShare</c> が false のとき何もしなかったが、それだと
    /// 畳んで開いたときに 0 のまま戻らない
    /// </para>
    /// </summary>
    private void RestoreShare()
    {
        if (DataContext is not MainViewModel vm) return;

        CalendarRow.MinHeight = 120;

        if (!vm.HasPaneCalendarShare)
        {
            CalendarRow.Height = GridLength.Auto;
            return;
        }

        var share = vm.PaneCalendarShare;

        CalendarRow.Height = new GridLength(share, GridUnitType.Star);
        ListRow.Height = new GridLength(1 - share, GridUnitType.Star);
    }

    /// <summary>
    /// 月カレンダーの折りたたみを切り替える（項目2）。
    /// <para>見出しの行そのものを押せるようにしてある（XAML の <c>MouseLeftButtonDown</c>）。</para>
    /// </summary>
    private void OnMonthHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.IsPaneCalendarCollapsed = !vm.IsPaneCalendarCollapsed;
        ApplyCollapsedState();
        e.Handled = true;
    }

    /// <summary>
    /// 畳み・開きの見た目を反映する（項目2）。
    /// <para>
    /// 畳んだときは <c>CalendarRow</c> の Height／MinHeight を 0 にし、月カレンダーと
    /// 仕切り（<c>Split</c>）を <see cref="Visibility.Collapsed"/> にする。
    /// <c>Collapsed</c> は当たり判定ごと消えるので、仕切りが掴めてしまう心配は無い
    /// （<c>IsHitTestVisible</c> を別に切る必要が無い）。空いた高さはそのまま
    /// <c>ListRow</c>（<c>Height="*"</c>）に回る。
    /// </para>
    /// <para>
    /// 開いたときは <see cref="RestoreShare"/> を呼び直すので、つまんで変えた
    /// 高さ・まだ変えていない Auto の高さのどちらも、畳む前と同じに戻る。
    /// </para>
    /// </summary>
    private void ApplyCollapsedState()
    {
        if (DataContext is not MainViewModel vm) return;

        if (vm.IsPaneCalendarCollapsed)
        {
            CalendarRow.Height = new GridLength(0);
            CalendarRow.MinHeight = 0;
            PaneMonthView.Visibility = Visibility.Collapsed;
            Split.Visibility = Visibility.Collapsed;
            Split.IsEnabled = false;
        }
        else
        {
            PaneMonthView.Visibility = Visibility.Visible;
            Split.Visibility = Visibility.Visible;
            Split.IsEnabled = true;
            RestoreShare();
        }
    }

    /// <summary>いまの割り振りを控える。</summary>
    private void SaveShare()
    {
        if (DataContext is not MainViewModel vm) return;

        var total = CalendarRow.ActualHeight + ListRow.ActualHeight;

        if (total <= 0) return;

        vm.PaneCalendarShare = CalendarRow.ActualHeight / total;
    }

    /// <summary>
    /// 本体（<c>MainWindow</c>）から、クイック入力欄へフォーカスするために呼ぶ（項目4）。
    /// <para>中身の <see cref="DayPaneView"/>（<see cref="DayPane"/>）へそのまま委ねる。</para>
    /// </summary>
    public void FocusQuickInput() => DayPane.FocusQuickInput();

    /// <summary>
    /// 選んだ日の予定・タスクの部品（項目1）。<c>MainWindow.xaml.cs</c> の
    /// Delete キー処理（<c>DeletePointedRow</c>）が、いま指している行を
    /// こちら側からも消せるように公開する。
    /// </summary>
    public DayPaneView DayPane => DayPaneHost;
}
