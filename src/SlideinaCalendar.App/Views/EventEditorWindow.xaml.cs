using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideinaCalendar.Presentation.Editing;
using SlideinaCalendar.Presentation.Infrastructure;

namespace SlideinaCalendar.App.Views;

/// <summary>予定の編集画面。入力は <see cref="EventEditorViewModel"/> が持つ。</summary>
public partial class EventEditorWindow : Window
{
    /// <summary>上下キーとホイールで動かす幅。時間割は15分単位で考えることが多い。</summary>
    private const int NudgeMinutes = 15;

    private readonly EventEditorViewModel _editor;

    public EventEditorWindow(EventEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        InitializeComponent();
        DataContext = _editor = editor;

        SaveCommand = new RelayCommand(() => DialogResult = true, () => editor.CanSave);

        // RelayCommand は CommandManager に乗っていないので、自分で知らせないと
        // 「保存できるようになったのにボタンが戻らない」状態のままになる
        editor.PropertyChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();

        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>保存して閉じる。取り消しは「取り消し」ボタンの IsCancel が受ける。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>上下キーで15分ずつ動かす。打ち直すより速い。</summary>
    /// <summary>
    /// 一覧から選んだら、その場で伝える。
    /// <para>
    /// 時刻の欄は打ち込みの途中で整形されないよう、フォーカスが外れるまで
    /// 伝えない作りにしてある。選んだときまで待たせると、開始を選んでも
    /// 終了が動かないように見える。
    /// </para>
    /// </summary>
    private void OnTimeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box) return;

        // 終了の候補は「10:00（1時間）」と長さを添えて並べている。
        // 欄に入れるのは時刻のほうだけ
        if (box.SelectedItem is EndTimeOption option) box.Text = option.Time;

        box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    private void OnTimeKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Up => NudgeMinutes,
            Key.Down => -NudgeMinutes,
            _ => 0,
        };

        // 一覧が開いているときは、上下で候補を選ぶほうが自然
        if (step == 0 || sender is not ComboBox { IsDropDownOpen: false } box) return;

        Nudge(box, step);
        e.Handled = true;
    }

    /// <summary>ホイールでも同じだけ動かす。</summary>
    private void OnTimeWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false, IsKeyboardFocusWithin: true } box) return;

        Nudge(box, e.Delta > 0 ? NudgeMinutes : -NudgeMinutes);
        e.Handled = true;
    }

    private void Nudge(ComboBox box, int minutes)
    {
        // 打ちかけの文字を先に取り込んでから動かす。動かした結果が打った値を無視しないように
        box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();

        // 単日と複数日で欄が分かれるので、名前ではなく印で見分ける
        if (box.Tag as string == "End") _editor.NudgeEnd(minutes);
        else _editor.NudgeStart(minutes);
    }
}
