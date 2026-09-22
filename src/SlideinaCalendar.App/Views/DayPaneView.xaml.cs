using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.App.Views;

/// <summary>
/// 選んだ日の予定・タスクをまとめた部品（項目1）。
/// <para>
/// 右パネル（<c>MainWindow.xaml</c> の旧 DetailPane）とスリムパネル
/// （<c>SidebarLayout.xaml</c>）が別々に持っていた同じ中身をここへ一本化した。
/// スリムパネルの月カレンダーを畳むと右パネルとまったく同じ並び（日付ヘッダ＋
/// クイック入力＋予定の節＋タスクの節＋期限なしタスクの節）になることに気づいた
/// ことがきっかけ。以後、両方の直しはこのファイル1つで済む。
/// </para>
/// <para>
/// <b>DataContext の受け方。</b>この <see cref="UserControl"/> 自身の
/// DataContext はホスト（<c>MainWindow</c> 直下でも <c>SidebarLayout</c> 越しでも）
/// から渡ってくる <c>MainViewModel</c> のまま変えない。中身（XAML の根の
/// <c>Grid</c>）だけを <c>SelectedDay</c> へ差し替え、コマンド類はそこから
/// <c>RelativeSource AncestorType={x:Type views:DayPaneView}</c> で自分自身
/// （＝差し替え前の DataContext）まで登って引く。<c>AncestorType=Window</c> に
/// 頼らないので、この部品が <c>SidebarLayout</c> にもう1段包まれていても
/// 経路は変わらない（右パネルが直に Window の子だったときと同じ1段登るだけで
/// 済む）。
/// </para>
/// </summary>
public partial class DayPaneView : UserControl
{
    /// <summary>
    /// 日送り（◀ ▶）を出すか。
    /// <para>
    /// 右パネルは <c>MainViewModel.ShowsDayNav</c>、スリムパネルは
    /// <c>ShowsSlimDayNav</c> と、ホストごとに見る条件が違う
    /// （中央のカレンダーを畳んでいるときだけ出す点は同じだが、右パネルは
    /// さらに「右パネル自身が開いている」ことも見る）。ViewModel 側を変えずに
    /// 済むよう、この部品はどちらの条件か知らない依存関係プロパティとして持ち、
    /// ホスト側の XAML がどちらのプロパティを繋ぐかだけを決める。
    /// </para>
    /// </summary>
    public static readonly DependencyProperty ShowsDayNavProperty = DependencyProperty.Register(
        nameof(ShowsDayNav), typeof(bool), typeof(DayPaneView), new PropertyMetadata(false));

    public bool ShowsDayNav
    {
        get => (bool)GetValue(ShowsDayNavProperty);
        set => SetValue(ShowsDayNavProperty, value);
    }

    /// <summary>
    /// 幅が狭いときに詰めた形にするか。
    /// <para>
    /// 月ビュー（<c>MonthView.xaml.cs</c> の <c>IsCompact</c>）と同じ考え方。
    /// スリムパネルは160px近くまで、右パネルは640pxまで動くので、1つの XAML で
    /// 両方の幅を受け止めるには、狭いときだけ詰める仕掛けが要る。ここでは
    /// ViewModel を経由せず、この部品だけが持つ依存関係プロパティとして
    /// 素朴に幅を見る（月ビューのように件数を数える必要が無いぶん単純）。
    /// </para>
    /// </summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(DayPaneView), new PropertyMetadata(false));

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    /// <summary>この幅を下回ったら詰めた形にする。実機で確かめていない目安値（報告に記載）。</summary>
    private const double CompactWidthThreshold = 260;

    private readonly Settle _settle;

    public DayPaneView()
    {
        InitializeComponent();

        _settle = new Settle(() => IsCompact = ActualWidth > 0 && ActualWidth < CompactWidthThreshold);
        SizeChanged += (_, _) => _settle.Poke();
    }

    /// <summary>本体（MainWindow / SidebarLayout）から、クイック入力欄へフォーカスするために呼ぶ（項目4）。</summary>
    public void FocusQuickInput()
    {
        QuickInputBox.Focus();
        Keyboard.Focus(QuickInputBox);
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

    // ------------------------------------------------------------------
    // 予定・タスクの行。マウスで指している行を控え、Delete キーで消せるように
    // する（項目8。もとは右パネルだけの機能。同じ行の作りを共有するので、
    // 一本化のついでにスリムパネルでも使えるようになる）
    // ------------------------------------------------------------------

    /// <summary>いまマウスが指している行。Delete キー（項目8）が使う。</summary>
    private object? _pointedRow;

    private void OnRowPointerEntered(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: { } item }) _pointedRow = item;
    }

    private void OnRowPointerExited(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: { } item } && ReferenceEquals(_pointedRow, item))
        {
            _pointedRow = null;
        }
    }

    /// <summary>
    /// マウスが指している行を消す。何も指していなければ何もしない。
    /// <para>
    /// 本体（<c>MainWindow.xaml.cs</c>）の Window レベルの Delete キー処理から
    /// 呼ぶ。右パネル用・スリムパネル用の2つの <see cref="DayPaneView"/> が
    /// 同時に画面に出ていることがあるので、「消せたか」を bool で返し、
    /// 呼び出し側がもう一方も試せるようにする。
    /// </para>
    /// </summary>
    public bool DeleteHoveredRow(MainViewModel vm)
    {
        switch (_pointedRow)
        {
            case DayEventViewModel ev when vm.DeleteEventCommand.CanExecute(ev):
                vm.DeleteEventCommand.Execute(ev);
                return true;

            case TaskListItemViewModel task when vm.DeleteTaskCommand.CanExecute(task):
                vm.DeleteTaskCommand.Execute(task);
                return true;

            default:
                return false;
        }
    }

    private void OnEventClicked(object sender, MouseButtonEventArgs e) =>
        Open<DayEventViewModel>(sender, e, (main, item) => main.EditEventCommand.Execute(item));

    private void OnTaskClicked(object sender, MouseButtonEventArgs e) =>
        Open<TaskListItemViewModel>(sender, e, (main, item) => main.EditTaskCommand.Execute(item));

    /// <summary>日付ラベル（マイルストーン）を2回押すと編集を開く。</summary>
    private void OnMilestoneClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not MilestoneViewModel milestone) return;
        if (DataContext is not MainViewModel main) return;

        main.EditMilestoneCommand.Execute(milestone);
        e.Handled = true;
    }

    /// <summary>ダブルクリックで編集。1回押しは選ぶだけにする。</summary>
    private void Open<T>(object sender, MouseButtonEventArgs e, Action<MainViewModel, T> open) where T : class
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not T item) return;
        if (DataContext is not MainViewModel main) return;

        open(main, item);
        e.Handled = true;
    }
}
