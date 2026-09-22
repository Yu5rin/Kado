using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// サイドバーモードの中身。
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
    /// 一度もつまんで変えていない（<see cref="MainViewModel.HasSlimCalendarShare"/>
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
    /// <c>HasSlimCalendarShare</c> が false のとき何もしなかったが、それだと
    /// 畳んで開いたときに 0 のまま戻らない
    /// </para>
    /// </summary>
    private void RestoreShare()
    {
        if (DataContext is not MainViewModel vm) return;

        CalendarRow.MinHeight = 120;

        if (!vm.HasSlimCalendarShare)
        {
            CalendarRow.Height = GridLength.Auto;
            return;
        }

        var share = vm.SlimCalendarShare;

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

        vm.IsSlimCalendarCollapsed = !vm.IsSlimCalendarCollapsed;
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

        if (vm.IsSlimCalendarCollapsed)
        {
            CalendarRow.Height = new GridLength(0);
            CalendarRow.MinHeight = 0;
            SlimMonthView.Visibility = Visibility.Collapsed;
            Split.Visibility = Visibility.Collapsed;
            Split.IsEnabled = false;
        }
        else
        {
            SlimMonthView.Visibility = Visibility.Visible;
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

        vm.SlimCalendarShare = CalendarRow.ActualHeight / total;
    }

    /// <summary>クイック入力は Enter で入れる。Esc は打ちかけを消してフォーカスを外す（項目17）。</summary>
    private void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel main) main.QuickText = string.Empty;

            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel vm || !vm.QuickCommand.CanExecute(null)) return;

        vm.QuickCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>本体（<c>MainWindow</c>）から、クイック入力欄へフォーカスするために呼ぶ（項目4）。</summary>
    public void FocusQuickInput()
    {
        QuickInputBox.Focus();
        Keyboard.Focus(QuickInputBox);
    }

    private void OnEventClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditEventCommand.Execute(item));

    private void OnTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open(sender, e, (main, item) => main.EditTaskCommand.Execute(item));

    /// <summary>
    /// 日付ラベル（マイルストーン）を2回押すと編集を開く。
    /// <para>右ペイン・月ビューと同じ挙動に揃える（項目9）。</para>
    /// </summary>
    private void OnMilestoneClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not MilestoneViewModel milestone) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        main.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

    /// <summary>ダブルクリックで編集。1回押しは選ぶだけにする。</summary>
    private static void Open(object sender, MouseButtonEventArgs e, Action<MainViewModel, object> open)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement element || element.DataContext is not { } item) return;
        if (Window.GetWindow(element)?.DataContext is not MainViewModel main) return;

        open(main, item);
        e.Handled = true;
    }
}
