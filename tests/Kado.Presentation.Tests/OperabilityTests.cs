using Kado.Data.Models;
using Kado.Google.Sync;
using Kado.Presentation.Editing;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;
using Kado.Presentation.Sync;
using Kado.Presentation.Update;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

/// <summary>操作性の直し（キー・確認・知らせ・設定の開き方・ツールチップ）の判断を試す。</summary>
public class OperabilityTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    // ------------------------------------------------------------------
    // フォーカスのある部品が使うキー（KeyFocusPolicy）。WPF の型は使えないので、同じ名前の偽の型で確かめる
    // ------------------------------------------------------------------

    private class Control;

    private class TextBoxBase : Control;

    private class TextBox : TextBoxBase;

    private class ComboBox : Control;

    private class ButtonBase : Control;

    private class Button : ButtonBase;

    private class ToggleButton : ButtonBase;

    private class CheckBox : ToggleButton;

    private class GridSplitter : Control;

    private class MenuItem : Control;

    private class Slider : Control;

    private class Selector : Control;

    private class ListBox : Selector;

    private class Border : Control;

    [Theory]
    [InlineData(typeof(Button))]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(ToggleButton))]
    [InlineData(typeof(GridSplitter))]
    [InlineData(typeof(MenuItem))]
    [InlineData(typeof(Slider))]
    [InlineData(typeof(ListBox))]
    public void 操作部品にフォーカスがあるときは_Enterや矢印を奪わない(Type focused)
    {
        Assert.True(KeyFocusPolicy.TakesNavigationKeys(focused));

        // 文字を打つ部品ではない（T や数字は、ここでは本体のキーとして効く）
        Assert.False(KeyFocusPolicy.IsTextInput(focused));
    }

    [Theory]
    [InlineData(typeof(TextBox))]
    [InlineData(typeof(ComboBox))]
    public void 文字を打つ部品は_単独キーも奪わない(Type focused)
    {
        Assert.True(KeyFocusPolicy.IsTextInput(focused));
        Assert.True(KeyFocusPolicy.TakesNavigationKeys(focused));
    }

    [Theory]
    [InlineData(typeof(Border))]
    [InlineData(typeof(Control))]
    public void 何も使わない部品や何も無いときは_本体のキーが効く(Type focused)
    {
        Assert.False(KeyFocusPolicy.TakesNavigationKeys(focused));
        Assert.False(KeyFocusPolicy.IsTextInput(focused));
    }

    [Fact]
    public void フォーカスが無ければ本体のキーが効く()
    {
        Assert.False(KeyFocusPolicy.TakesNavigationKeys(null));
        Assert.False(KeyFocusPolicy.IsTextInput(null));
    }

    // ------------------------------------------------------------------
    // 繰り返しの予定を消す前の確認
    // ------------------------------------------------------------------

    private static CalendarEvent Recurring(string id) => new()
    {
        Id = id, Title = "定例会議", Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
        Recurrence = "FREQ=WEEKLY;BYDAY=TH",
    };

    private static CalendarEvent Single(string id) => new()
    {
        Id = id, Title = "打ち合わせ", Date = Today,
        StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0),
    };

    [Fact]
    public void 繰り返しの予定を消す前に確かめる_削除とキャンセルの文言で出す()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1"));
        var editors = new FakeEditorPresenter { Confirms = false };
        var main = new MainViewModel(test.Workspace, Today, editors: editors);

        main.DeleteEventCommand.Execute(Assert.Single(main.SelectedDay.Events));

        Assert.Equal(1, editors.LabeledConfirmAsked);
        Assert.Equal("繰り返しの予定を、すべての回、削除します。", editors.LastConfirmMessage);
        Assert.Equal("削除", editors.LastAcceptLabel);
        Assert.Equal("キャンセル", editors.LastCancelLabel);

        // キャンセルなら消えない
        Assert.NotNull(test.Workspace.Events.Find("r1"));
    }

    [Fact]
    public void 繰り返しの予定の確認で削除を選ぶと消える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1"));
        var main = new MainViewModel(test.Workspace, Today, editors: new FakeEditorPresenter { Confirms = true });

        main.DeleteEventCommand.Execute(Assert.Single(main.SelectedDay.Events));

        Assert.Null(test.Workspace.Events.Find("r1"));
        Assert.Equal("繰り返しの予定をすべての回、削除しました", main.StatusMessage);
    }

    [Fact]
    public void 繰り返しでない予定は今までどおり確認なしで消える()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Single("e1"));
        var editors = new FakeEditorPresenter { Confirms = false };
        var main = new MainViewModel(test.Workspace, Today, editors: editors);

        main.DeleteEventCommand.Execute(Assert.Single(main.SelectedDay.Events));

        Assert.Equal(0, editors.LabeledConfirmAsked);
        Assert.Null(test.Workspace.Events.Find("e1"));
    }

    [Fact]
    public void 編集画面の削除から来ても_繰り返しの予定は確かめる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Recurring("r1"));
        var editors = new FakeEditorPresenter
        {
            Confirms = false,
            OnEvent = editor =>
            {
                editor.RequestDelete();
                return false;
            },
        };
        var main = new MainViewModel(test.Workspace, Today, editors: editors);

        main.EditEventCommand.Execute(Assert.Single(main.SelectedDay.Events));

        Assert.Equal(1, editors.LabeledConfirmAsked);
        Assert.NotNull(test.Workspace.Events.Find("r1"));
    }

    // ------------------------------------------------------------------
    // 編集画面を閉じるとき、変更があるか（IsDirty）
    // ------------------------------------------------------------------

    private static readonly SourceChoice[] Choices = [new("local:a", "仕事"), new("local:b", "私用")];

    [Fact]
    public void 予定は開いたままなら変更なし_変えると変更あり_元に戻せば変更なし()
    {
        var vm = new EventEditorViewModel(Today, Choices);
        Assert.False(vm.IsDirty);

        vm.Title = "会議";
        Assert.True(vm.IsDirty);

        vm.Title = string.Empty;
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void 既存の予定を開いただけでは変更なし_メモや日付を変えると変更あり()
    {
        var stored = Single("e1") with { CalendarId = "local:a" };
        var vm = new EventEditorViewModel(stored, Choices);
        Assert.False(vm.IsDirty);

        vm.Note = "持ち物";
        Assert.True(vm.IsDirty);

        vm.Note = null;
        Assert.False(vm.IsDirty);

        vm.StartTimeText = "11:00";
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void 読み取り専用の予定は変えられないので変更なし()
    {
        var vm = new EventEditorViewModel(Single("e1"), Choices, readOnlyReason: "読み取り専用");

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void タスクの変更を見分ける()
    {
        var vm = new TaskEditorViewModel(Today, [new SourceChoice("list-a", "リスト")], Today);
        Assert.False(vm.IsDirty);

        vm.Title = "提出";
        Assert.True(vm.IsDirty);

        vm.Title = string.Empty;
        Assert.False(vm.IsDirty);

        vm.HasDue = false;
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void カレンダーの変更を見分ける()
    {
        var created = new CalendarEditorViewModel();
        Assert.False(created.IsDirty);

        created.Name = "仕事";
        Assert.True(created.IsDirty);

        var existing = new CalendarEditorViewModel("c1", "仕事", "#336699");
        Assert.False(existing.IsDirty);

        existing.Color = "#aa0000";
        Assert.True(existing.IsDirty);

        existing.Color = "#336699";
        Assert.False(existing.IsDirty);
    }

    // ------------------------------------------------------------------
    // ◀ ▶ のツールチップ
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(CalendarView.Month, "前の月", "次の月")]
    [InlineData(CalendarView.Week, "前の週", "次の週")]
    [InlineData(CalendarView.Day, "前の日", "次の日")]
    [InlineData(CalendarView.Year, "前の年度", "次の年度")]
    [InlineData(CalendarView.Agenda, "前の月", "次の月")]
    public void 送りのツールチップはビューの単位に合う(CalendarView view, string previous, string next)
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today) { CurrentView = view };

        Assert.Equal(previous, main.PreviousToolTip);
        Assert.Equal(next, main.NextToolTip);
    }

    [Fact]
    public void ビューを切り替えるとツールチップの変更が伝わる()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.CurrentView = CalendarView.Week;

        Assert.Contains(nameof(MainViewModel.PreviousToolTip), changed);
        Assert.Contains(nameof(MainViewModel.NextToolTip), changed);
    }

    [Fact]
    public void 中央を畳んでいるときの送りは日()
    {
        Assert.Equal("前の日", MainViewModel.NavigationLabel("前の", CalendarView.Year, mainViewOpen: false));
    }

    // ------------------------------------------------------------------
    // 知らせの種類（成功・失敗）
    // ------------------------------------------------------------------

    [Fact]
    public void 失敗の知らせは種類が失敗で_成功の知らせは成功()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);

        main.AnnounceStatus("保存しました");
        Assert.Equal(StatusKind.Success, main.StatusKind);
        Assert.False(main.IsStatusFailure);

        main.AnnounceFailure("書き出せませんでした");
        Assert.Equal(StatusKind.Failure, main.StatusKind);
        Assert.True(main.IsStatusFailure);
    }

    [Fact]
    public void 失敗の知らせは次の知らせか_バツを押すまで残る()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        main.DismissOnboardingCommand.Execute(null);

        main.AnnounceFailure("書き出せませんでした");

        // 画面側は失敗の知らせを自動では消さない。ここでは次の知らせで置き換わることを確かめる
        main.AnnounceStatus("保存しました");
        Assert.False(main.IsStatusFailure);
        Assert.Equal("保存しました", main.StatusMessage);

        main.AnnounceFailure("またできませんでした");
        main.DismissStatusCommand.Execute(null);

        Assert.Null(main.StatusMessage);
        Assert.False(main.IsStatusFailure);
        Assert.False(main.ShowsStatusPill);
    }

    [Fact]
    public void 同じ文でも失敗から成功に変わった種類は伝わる()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        var changed = 0;
        main.StatusChanged += (_, _) => changed++;

        main.AnnounceFailure("同じ文");
        main.AnnounceStatus("同じ文");

        Assert.Equal(2, changed);
        Assert.False(main.IsStatusFailure);
    }

    [Fact]
    public void 見つからなかった_できなかったは失敗の知らせになる()
    {
        using var test = TestWorkspace.Create();
        test.Workspace.AddEvent(Single("e1"));
        var main = new MainViewModel(test.Workspace, Today, editors: new FakeEditorPresenter());

        // 画面に出ている行が指す予定が、手元から先に消えていた
        var row = Assert.Single(main.SelectedDay.Events);
        test.Workspace.DeleteEvent("e1");
        main.DeleteEventCommand.Execute(row);

        Assert.Equal("予定が見つかりませんでした", main.StatusMessage);
        Assert.True(main.IsStatusFailure);
    }

    // ------------------------------------------------------------------
    // ウィンドウのとき、寄せる辺を選んだら
    // ------------------------------------------------------------------

    [Fact]
    public void ウィンドウで左端を選ぶと_スライドにしたときの話だと知らせる()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        Assert.Equal(ShellMode.Window, main.Shell.Mode);

        main.Shell.EdgeLeftCommand.Execute(null);
        Assert.Equal("スライドにしたとき左端から出ます", main.StatusMessage);

        main.Shell.EdgeRightCommand.Execute(null);
        Assert.Equal("スライドにしたとき右端から出ます", main.StatusMessage);
        Assert.True(main.Shell.IsAtRight);
    }

    [Fact]
    public void スライドで端を選ぶときは_すでに効くので知らせない()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        main.Shell.Mode = ShellMode.Overlay;
        main.ClearStatusMessage();

        main.Shell.EdgeRightCommand.Execute(null);

        Assert.Null(main.StatusMessage);
    }

    [Fact]
    public void 固定を選ぶと固定になり_外すと留める前の出しかたに戻る()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        main.Shell.Mode = ShellMode.Overlay;

        main.Shell.ToDockCommand.Execute(null);
        Assert.Equal(ShellMode.Dock, main.Shell.Mode);

        // すでに固定なら、そのまま
        main.Shell.ToDockCommand.Execute(null);
        Assert.Equal(ShellMode.Dock, main.Shell.Mode);

        main.Shell.TogglePinCommand.Execute(null);
        Assert.Equal(ShellMode.Overlay, main.Shell.Mode);
    }

    // ------------------------------------------------------------------
    // 同期のボタン・設定を分類を指定して開く
    // ------------------------------------------------------------------

    private sealed class StubGoogle : IGoogleSync
    {
        public bool IsConnected { get; set; }

        public bool CanConnect { get; set; } = true;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public int SyncCount { get; private set; }

        public Task<SyncReport?> SyncAsync(CancellationToken cancellationToken = default)
        {
            SyncCount++;
            return Task.FromResult<SyncReport?>(new SyncReport());
        }

        public bool HasDriveAttachmentScope => false;

        public Task<bool> EnsureDriveAttachmentScopeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public GoogleDriveApi CreateDriveApi() => throw new NotSupportedException();
    }

    private static MainViewModel CreateWithSettings(
        TestWorkspace test, FakeEditorPresenter editors, IGoogleSync? google = null) =>
        new(test.Workspace, Today, editors: editors, google: google,
            settings: new AppSettings(test.Workspace.Settings));

    [Fact]
    public void 繋いでいないときに同期のボタンを押すと設定のGoogleが開く()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var google = new StubGoogle { IsConnected = false };
        var main = CreateWithSettings(test, editors, google);

        Assert.True(main.SyncButtonCommand.CanExecute(null));
        main.SyncButtonCommand.Execute(null);

        Assert.NotNull(editors.LastSettings);
        Assert.Equal(SettingsCategory.Google, editors.LastSettings!.Category);
        Assert.Equal(0, google.SyncCount);
    }

    [Fact]
    public async Task 繋いであれば同期のボタンは今すぐ同期する()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var google = new StubGoogle { IsConnected = true };
        var main = CreateWithSettings(test, editors, google);

        main.SyncButtonCommand.Execute(null);
        await Task.Delay(50);

        Assert.Null(editors.LastSettings);
        Assert.Equal(1, google.SyncCount);
    }

    [Fact]
    public void 設定は最後に開いていた分類を覚えている()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var main = CreateWithSettings(test, editors);

        main.OpenSettingsCommand.Execute(null);
        Assert.Equal(SettingsCategory.Startup, editors.LastSettings!.Category);

        // 画面で別の分類に移った
        editors.LastSettings.Category = SettingsCategory.Data;

        main.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsCategory.Data, editors.LastSettings!.Category);
    }

    [Fact]
    public void 分類を指定して開くと_次に開くときもそこから始まる()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var main = CreateWithSettings(test, editors);

        main.OpenSettings(SettingsCategory.Google);
        main.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsCategory.Google, editors.LastSettings!.Category);
    }

    [Fact]
    public void 設定の中で起きた操作の結果は設定の画面にも出る_失敗は赤の印つき()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var main = CreateWithSettings(test, editors);

        main.AnnounceFailure("前から残っていた失敗");
        main.OpenSettingsCommand.Execute(null);
        var settings = editors.LastSettings!;

        // 開く前から残っているものは見せない
        Assert.Null(settings.Notice);

        main.AnnounceStatus("バックアップを書き出しました");
        Assert.Equal("バックアップを書き出しました", settings.Notice);
        Assert.False(settings.NoticeIsFailure);

        main.AnnounceFailure("書き出せませんでした");
        Assert.Equal("書き出せませんでした", settings.Notice);
        Assert.True(settings.NoticeIsFailure);

        // 帯が自動で消えても、読む前に消さない
        main.ClearStatusMessage();
        Assert.Equal("書き出せませんでした", settings.Notice);
    }

    [Fact]
    public void 設定の画面を閉じたあとは本体の知らせを受けない()
    {
        using var test = TestWorkspace.Create();
        var editors = new FakeEditorPresenter();
        var main = CreateWithSettings(test, editors);

        main.OpenSettingsCommand.Execute(null);
        var settings = editors.LastSettings!;
        settings.Detach();

        main.AnnounceStatus("閉じたあとの知らせ");

        Assert.Null(settings.Notice);
    }

    // ------------------------------------------------------------------
    // 自動更新は、打ちかけの文字があるあいだは入れ替えない
    // ------------------------------------------------------------------

    [Fact]
    public void 打ちかけの文字があるあいだは入れ替えを待つ()
    {
        var state = new AutoUpdateState(
            Enabled: true, HasStaged: true, Busy: false, OtherWindowOpen: false, ModalDialogOpen: false,
            MenuOrPopupOpen: false, DragInProgress: false, SyncRunning: false,
            IdleFor: TimeSpan.FromMinutes(30), HasDraftInput: true);

        Assert.Equal(AutoUpdateWait.DraftInput, AutoUpdatePolicy.Wait(state));
        Assert.False(AutoUpdatePolicy.CanApplyNow(state));
        Assert.Contains("打ちかけ", AutoUpdatePolicy.Describe(AutoUpdateWait.DraftInput));
    }

    [Fact]
    public void 打ちかけが無ければ_これまでどおり入れ替えてよい()
    {
        var state = new AutoUpdateState(
            Enabled: true, HasStaged: true, Busy: false, OtherWindowOpen: false, ModalDialogOpen: false,
            MenuOrPopupOpen: false, DragInProgress: false, SyncRunning: false,
            IdleFor: TimeSpan.FromMinutes(30));

        Assert.Equal(AutoUpdateWait.None, AutoUpdatePolicy.Wait(state));
    }

    [Fact]
    public void クイック入力か検索に文字があれば打ちかけとみなす()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        Assert.False(main.HasDraftInput);

        main.QuickText = "明日15時 打合せ";
        Assert.True(main.HasDraftInput);

        main.QuickText = "   ";
        Assert.False(main.HasDraftInput);

        main.SearchText = "会議";
        Assert.True(main.HasDraftInput);

        main.SearchText = string.Empty;
        Assert.False(main.HasDraftInput);
    }

    // ------------------------------------------------------------------
    // メニューのコマンドがそろっている
    // ------------------------------------------------------------------

    [Fact]
    public void 終了を求めるとApp側に伝わる()
    {
        using var test = TestWorkspace.Create();
        var main = new MainViewModel(test.Workspace, Today);
        var requested = 0;
        main.QuitRequested += (_, _) => requested++;

        main.QuitCommand.Execute(null);

        Assert.Equal(1, requested);
    }
}
