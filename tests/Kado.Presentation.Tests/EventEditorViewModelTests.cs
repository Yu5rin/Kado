using Kado.Data.Models;
using Kado.Presentation.Editing;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Tests;

public class EventEditorViewModelTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>カレンダー欄の候補。名前で選ばせ、保存するのは ID。</summary>
    private static readonly SourceChoice[] Calendars = [new("local:shigoto", "仕事")];

    private static EventEditorViewModel New() => new(D(2026, 9, 24), Calendars);

    [Fact]
    public void タイトルが空なら保存できない()
    {
        var vm = New();

        Assert.False(vm.CanSave);
        Assert.Equal("タイトルを入れてください。", vm.ValidationMessage);

        vm.Title = "会議";
        Assert.True(vm.CanSave);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void 読めない時刻は保存できない()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "きゅうじ";

        Assert.False(vm.CanSave);
        Assert.Contains("開始時刻", vm.ValidationMessage);
    }

    [Fact]
    public void 終了が開始より前なら保存できない()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "13:00";
        vm.EndTimeText = "10:00";

        Assert.Contains("終了時刻", vm.ValidationMessage);
    }

    [Fact]
    public void 終日なら時刻は見ない()
    {
        var vm = New();
        vm.Title = "棚卸";
        vm.StartTimeText = "でたらめ";
        vm.IsAllDay = true;

        Assert.True(vm.CanSave);

        var model = vm.ToModel();
        Assert.Null(model.StartTime);
        Assert.Null(model.EndTime);
        Assert.True(model.IsAllDay);
    }

    [Fact]
    public void 日をまたぐ予定は時刻の逆転を許す()
    {
        var vm = New();
        vm.Title = "夜勤";
        vm.StartTimeText = "22:00";
        vm.EndTimeText = "06:00";

        // 単日なら打ち間違い
        Assert.False(vm.CanSave);

        // 終了日を後ろにすればまたがる予定になる
        vm.EndDate = D(2026, 9, 25);

        Assert.True(vm.IsMultiDay);
        Assert.True(vm.CanSave);
        Assert.Equal(D(2026, 9, 25), vm.ToModel().EndDate);
    }

    [Fact]
    public void 終了日を開始日より前にすると開始日に寄せる()
    {
        var vm = New();
        vm.Title = "棚卸";
        vm.IsAllDay = true;

        // 前の日を選んでも、保存できない状態にはしない
        vm.EndDate = D(2026, 9, 20);

        Assert.Equal(vm.Date, vm.EndDate);
        Assert.False(vm.IsMultiDay);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void 終了日が同じ日なら1日の予定()
    {
        var vm = New();
        vm.Title = "棚卸";

        Assert.False(vm.IsMultiDay);
        Assert.Null(vm.ToModel().EndDate);
    }

    [Fact]
    public void 開始日を後ろへ動かすと終了日も付いてくる()
    {
        var vm = New();
        vm.EndDate = D(2026, 9, 25);   // 開始は 9/24。2日にまたがる

        vm.Date = D(2026, 9, 28);

        // またがる日数はそのまま。取り残して「終了日が前」の状態にしない
        Assert.Equal(D(2026, 9, 29), vm.EndDate);
        Assert.True(vm.IsMultiDay);
    }

    [Fact]
    public void 既存を直しても識別子と繰り返しは残る()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
            Recurrence = "FREQ=WEEKLY", GoogleEventId = "g1", Source = "google",
        };

        var vm = new EventEditorViewModel(source, Calendars);
        Assert.False(vm.IsNew);
        Assert.Equal("予定の編集", vm.HeaderText);
        Assert.Equal("09:00", vm.StartTimeText);

        vm.Title = "定例（変更）";
        var model = vm.ToModel();

        Assert.Equal("e1", model.Id);
        Assert.Equal("定例（変更）", model.Title);
        // 編集画面で触っていない項目を消さない
        Assert.Equal("FREQ=WEEKLY", model.Recurrence);
        Assert.Equal("g1", model.GoogleEventId);
        Assert.Equal("google", model.Source);
    }

    [Fact]
    public void 色は選ばせない()
    {
        // 色は所属カレンダーで決まる（要件どおりの運用）。1件ずつは選ばせない
        Assert.Null(typeof(EventEditorViewModel).GetProperty("Accent"));
    }

    [Fact]
    public void 取り込んだ予定が持つ色は消さない()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), Color = "#d1fae5",
        };

        var vm = new EventEditorViewModel(source, []) { Title = "会議（変更）" };

        // 画面に色の欄が無いからといって、持っている値を落とさない
        Assert.Equal("#d1fae5", vm.ToModel().Color);
    }

    [Fact]
    public void URLを持てる()
    {
        var vm = New();
        vm.Title = "図面レビュー";
        vm.Url = " https://example.com/drawing ";

        // 説明欄に書くと本文と混ざって拾いにくい
        Assert.Equal("https://example.com/drawing", vm.ToModel().Url);
    }

    [Fact]
    public void 空欄はnullにして保存する()
    {
        var vm = New();
        vm.Title = "  会議  ";
        vm.Location = "   ";
        vm.Note = "";

        var model = vm.ToModel();

        Assert.Equal("会議", model.Title);
        Assert.Null(model.Location);
        Assert.Null(model.Note);
    }

    [Fact]
    public void 保存できない状態でToModelを呼ぶと弾かれる()
    {
        Assert.Throws<InvalidOperationException>(() => New().ToModel());
    }

    [Fact]
    public void 開始を動かすと終了も同じ長さのまま付いてくる()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "09:00";
        vm.EndTimeText = "10:30";

        vm.StartTimeText = "13:00";

        // 1時間30分のまま。終わりを打ち直させない
        Assert.Equal("14:30", vm.EndTimeText);
        Assert.Equal("1時間30分", vm.DurationText);
    }

    [Fact]
    public void 既にある予定でも開始を動かすと終了が付いてくる()
    {
        var vm = new EventEditorViewModel(new CalendarEvent
        {
            Id = "e1", Title = "課内会議", Date = new DateOnly(2026, 9, 14),
            StartTime = new TimeOnly(9, 30), EndTime = new TimeOnly(10, 0),
        }, null);

        vm.StartTimeText = "13:00";

        // 30分のまま後ろへずれる
        Assert.Equal("13:30", vm.EndTimeText);
    }

    [Fact]
    public void 候補から選んだ終了は時刻だけを残す()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "09:00";

        // 一覧には「10:00（1時間）」と出るが、欄に残るのは時刻だけ
        vm.EndTimeText = "10:00（1時間）";

        Assert.Equal("10:00", vm.EndTimeText);
    }

    [Fact]
    public void 打ち方はゆるく受ける()
    {
        var vm = New();
        vm.Title = "会議";
        vm.StartTimeText = "930";

        Assert.True(vm.CanSave);
        Assert.Equal(new TimeOnly(9, 30), vm.ToModel().StartTime);
    }

    [Fact]
    public void 終了の候補は長さを添えて出す()
    {
        var vm = New();
        vm.StartTimeText = "09:00";

        var options = vm.EndTimeOptions;

        Assert.Equal("09:15（15分）", options[0].Label);
        Assert.Equal("10:00（1時間）", options[3].Label);

        // 候補の表示をそのまま入れても読める
        vm.Title = "会議";
        vm.EndTimeText = options[5].Label;
        Assert.Equal(new TimeOnly(10, 30), vm.ToModel().EndTime);
    }

    [Fact]
    public void 上下で15分ずつ動かせる()
    {
        var vm = New();
        vm.StartTimeText = "09:00";

        vm.NudgeStart(15);
        Assert.Equal("09:15", vm.StartTimeText);

        vm.NudgeStart(-30);
        Assert.Equal("08:45", vm.StartTimeText);

        vm.NudgeEnd(60);
        Assert.Equal("10:45", vm.EndTimeText);
    }

    [Fact]
    public void 繰り返しは開始日に合わせた文言で出る()
    {
        var vm = New();   // 2026/9/24 は木曜

        Assert.Equal(["繰り返さない", "毎日", "毎週 木曜日", "毎月 24日", "毎年 9月24日"],
                     vm.RecurrenceOptions.Select(o => o.Label));

        vm.Date = D(2026, 9, 25);   // 金曜
        Assert.Equal("毎週 金曜日", vm.RecurrenceOptions[2].Label);
    }

    [Fact]
    public void 繰り返しは指定文字列と行き来する()
    {
        var vm = New();
        vm.Title = "週次定例";
        vm.Recurrence = RecurrenceKind.Weekly;

        var stored = vm.ToModel();
        Assert.Equal("FREQ=WEEKLY;BYDAY=TH", stored.Recurrence);

        // 読み直しても同じ選択に戻る
        Assert.Equal(RecurrenceKind.Weekly, new EventEditorViewModel(stored, []).Recurrence);
    }

    [Fact]
    public void 選択肢で表せない繰り返しは消さずに持ち続ける()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "隔週", Date = D(2026, 9, 24),
            Recurrence = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR",
        };

        var vm = new EventEditorViewModel(source, []);

        Assert.Equal(RecurrenceKind.Custom, vm.Recurrence);
        Assert.Contains(vm.RecurrenceOptions, o => o.Kind == RecurrenceKind.Custom);

        // タイトルだけ直して保存しても、繰り返しは元のまま
        vm.Title = "隔週（変更）";
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR", vm.ToModel().Recurrence);
    }

    [Fact]
    public void 繰り返しを外せる()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24), Recurrence = "FREQ=DAILY",
        };

        var vm = new EventEditorViewModel(source, []) { Recurrence = RecurrenceKind.None };

        Assert.Null(vm.ToModel().Recurrence);
    }

    [Fact]
    public void 繰り返しの選択肢は元が独自指定のときだけカスタムを出す()
    {
        Assert.DoesNotContain(New().RecurrenceOptions, o => o.Kind == RecurrenceKind.Custom);
    }

    [Fact]
    public void 読めた時刻は表記を揃える()
    {
        var vm = New();

        vm.StartTimeText = "930";
        Assert.Equal("09:30", vm.StartTimeText);

        // 候補から選んだときの添え字は落とす
        vm.EndTimeText = "10:30（1時間）";
        Assert.Equal("10:30", vm.EndTimeText);
    }

    [Fact]
    public void 読めない打ちかけは消さない()
    {
        var vm = New();
        vm.StartTimeText = "9:";

        // 勝手に直すと打ち直せなくなる
        Assert.Equal("9:", vm.StartTimeText);
    }

    [Fact]
    public void 新しい予定の開始はいまの次の30分区切り()
    {
        var vm = new EventEditorViewModel(new DateOnly(2026, 9, 24), Calendars, new TimeOnly(14, 12))
        {
            Title = "打ち合わせ",
        };

        Assert.Equal("14:30", vm.StartTimeText);
        Assert.Equal("15:30", vm.EndTimeText);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void 夜遅くに足しても終了が開始より前にならない()
    {
        // 23:30 に1時間足すと 0:30 になって逆転し、保存できなくなる
        var vm = new EventEditorViewModel(new DateOnly(2026, 9, 24), Calendars, new TimeOnly(23, 10))
        {
            Title = "夜の作業",
        };

        Assert.Equal("23:30", vm.StartTimeText);
        Assert.Equal("23:59", vm.EndTimeText);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void 時刻を渡さなければ今までどおり()
    {
        var vm = new EventEditorViewModel(new DateOnly(2026, 9, 24), Calendars);

        Assert.Equal("09:00", vm.StartTimeText);
        Assert.Equal("10:00", vm.EndTimeText);
    }

    [Fact]
    public void 長すぎるタイトルは保存できない()
    {
        var vm = New();
        vm.Title = new string('あ', 1025);

        Assert.False(vm.CanSave);
        Assert.Contains("タイトル", vm.ValidationMessage);
    }

    [Fact]
    public void 長すぎる説明は保存できない()
    {
        var vm = New();
        vm.Title = "会議";
        vm.Note = new string('あ', 8193);

        Assert.False(vm.CanSave);
        Assert.Contains("説明", vm.ValidationMessage);
    }

    [Fact]
    public void 新規作成では削除を求めても効かない()
    {
        var vm = New();
        vm.RequestDelete();

        Assert.False(vm.Deleted);
    }

    [Fact]
    public void 既存の予定は削除を求められる()
    {
        var source = new CalendarEvent { Id = "e1", Title = "定例", Date = D(2026, 9, 24) };
        var vm = new EventEditorViewModel(source, Calendars);

        Assert.False(vm.Deleted);
        vm.RequestDelete();

        Assert.True(vm.Deleted);
    }

    // ------------------------------------------------------------------
    // 「カレンダーに従う」の文言（実機の報告：従うとどちらになるか画面から分からない）
    // ------------------------------------------------------------------

    [Fact]
    public void 通知ONのカレンダーなら従うの結果は知らせる()
    {
        SourceChoice[] calendars = [new("local:shigoto", "仕事", Notifies: true)];
        var vm = new EventEditorViewModel(D(2026, 9, 24), calendars);
        vm.CalendarId = "local:shigoto";

        var follow = vm.NotifyOptions.Single(o => o.Value is null);

        Assert.Equal("カレンダーに従う（知らせる）", follow.Label);
    }

    [Fact]
    public void 通知OFFのカレンダーなら従うの結果は知らせない()
    {
        SourceChoice[] calendars = [new("local:private", "プライベート", Notifies: false)];
        var vm = new EventEditorViewModel(D(2026, 9, 24), calendars);
        vm.CalendarId = "local:private";

        var follow = vm.NotifyOptions.Single(o => o.Value is null);

        Assert.Equal("カレンダーに従う（知らせない）", follow.Label);
    }

    [Fact]
    public void カレンダーを選び直すと従うの文言も追従する()
    {
        SourceChoice[] calendars =
        [
            new("local:shigoto", "仕事", Notifies: true),
            new("local:private", "プライベート", Notifies: false),
        ];
        var vm = new EventEditorViewModel(D(2026, 9, 24), calendars, defaultCalendarId: "local:shigoto");

        Assert.Equal("カレンダーに従う（知らせる）",
            vm.NotifyOptions.Single(o => o.Value is null).Label);

        vm.CalendarId = "local:private";

        Assert.Equal("カレンダーに従う（知らせない）",
            vm.NotifyOptions.Single(o => o.Value is null).Label);
    }

    [Fact]
    public void 一覧に無いカレンダーIDのときは既定の知らせるに揃える()
    {
        var vm = New();
        vm.CalendarId = "存在しないID";

        var follow = vm.NotifyOptions.Single(o => o.Value is null);

        Assert.Equal("カレンダーに従う（知らせる）", follow.Label);
    }

    [Fact]
    public void 知らせる知らせないの選択肢はカレンダーによらず変わらない()
    {
        SourceChoice[] calendars = [new("local:private", "プライベート", Notifies: false)];
        var vm = new EventEditorViewModel(D(2026, 9, 24), calendars);
        vm.CalendarId = "local:private";

        Assert.Equal("知らせる", vm.NotifyOptions.Single(o => o.Value == true).Label);
        Assert.Equal("知らせない", vm.NotifyOptions.Single(o => o.Value == false).Label);
    }

    // ------------------------------------------------------------------
    // Google 側の情報を保存のたびに失わない
    //
    // GoogleRaw が消えると、次の同期で「一度も受け取っていない」扱いになり、
    // カスタムの繰り返し（RDATE など）が空の配列で送られて消える
    // ------------------------------------------------------------------

    [Fact]
    public void 保存してもGoogleRawとGoogleCalendarIdとStatusを失わない()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            GoogleEventId = "g1", GoogleCalendarId = "primary", Source = "google",
            GoogleRaw = """{"id":"g1","recurrence":["RRULE:FREQ=WEEKLY"]}""",
            Status = "confirmed",
        };

        var vm = new EventEditorViewModel(source, Calendars);
        vm.Title = "定例（変更）";

        var model = vm.ToModel();

        Assert.Equal(source.GoogleRaw, model.GoogleRaw);
        Assert.Equal("primary", model.GoogleCalendarId);
        Assert.Equal("confirmed", model.Status);
    }

    // ------------------------------------------------------------------
    // カレンダー欄
    // ------------------------------------------------------------------

    [Fact]
    public void 繰り返しの1回だけの回はカレンダーを変えられない()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "振替", Date = D(2026, 10, 2),
            CalendarId = "primary",
            GoogleEventId = "g1_20261001", GoogleCalendarId = "primary", Source = "google",
            GoogleRaw = """{"id":"g1_20261001","recurringEventId":"g1"}""",
        };

        SourceChoice[] calendars = [new("primary", "仕事"), new("secondary", "私用")];
        var vm = new EventEditorViewModel(source, calendars);

        Assert.False(vm.CanChangeCalendar);
        Assert.NotNull(vm.CalendarLockReason);

        vm.CalendarId = "secondary";

        // 変えようとしても変わらない
        Assert.Equal("primary", vm.CalendarId);
    }

    [Fact]
    public void ふつうの予定はカレンダーを変えられる()
    {
        var source = new CalendarEvent
        {
            Id = "e1", Title = "定例", Date = D(2026, 9, 24),
            GoogleEventId = "g1", GoogleCalendarId = "primary", Source = "google",
            GoogleRaw = """{"id":"g1"}""",
        };

        SourceChoice[] calendars = [new("primary", "仕事"), new("secondary", "私用")];
        var vm = new EventEditorViewModel(source, calendars);

        Assert.True(vm.CanChangeCalendar);
        Assert.Null(vm.CalendarLockReason);

        vm.CalendarId = "secondary";
        Assert.Equal("secondary", vm.CalendarId);
    }

    // ------------------------------------------------------------------
    // 添付
    // ------------------------------------------------------------------

    private static readonly SourceChoice[] GoogleCalendars = [new("primary", "仕事")];

    [Fact]
    public void ローカルだけのカレンダーでは添付を使えない()
    {
        var vm = New();

        Assert.False(vm.CanUseAttachments);
        Assert.NotNull(vm.AttachmentsDisabledReason);
    }

    [Fact]
    public void Google連携のカレンダーでは添付を使える()
    {
        var vm = new EventEditorViewModel(D(2026, 9, 24), GoogleCalendars) { Title = "会議" };

        Assert.True(vm.CanUseAttachments);
        Assert.Null(vm.AttachmentsDisabledReason);
    }

    [Fact]
    public async Task 添付を足すとPendingAttachmentsに入る()
    {
        var dialogs = new FakeFileDialogs { FileToPick = "/tmp/資料.pdf" };
        var uploader = new FakeAttachmentUploader
        {
            Result = AttachmentUploadResult.Success(
                new EventAttachment("file1", "https://drive.google.com/file/d/file1/view", "資料.pdf", "application/pdf")),
        };

        var vm = new EventEditorViewModel(D(2026, 9, 24), GoogleCalendars, uploader: uploader, dialogs: dialogs)
        {
            Title = "会議",
        };

        await vm.AddAttachmentAsync();

        Assert.Single(vm.Attachments);
        Assert.Equal("資料.pdf", vm.Attachments[0].Title);

        var model = vm.ToModel();
        Assert.NotNull(model.PendingAttachments);
        Assert.Contains("file1", model.PendingAttachments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task アップロードに失敗したら理由を出す()
    {
        var dialogs = new FakeFileDialogs { FileToPick = "/tmp/資料.pdf" };
        var uploader = new FakeAttachmentUploader
        {
            Result = AttachmentUploadResult.Failure("添付を足すにはドライブの権限が要ります。"),
        };

        var vm = new EventEditorViewModel(D(2026, 9, 24), GoogleCalendars, uploader: uploader, dialogs: dialogs)
        {
            Title = "会議",
        };

        await vm.AddAttachmentAsync();

        Assert.Empty(vm.Attachments);
        Assert.Equal("添付を足すにはドライブの権限が要ります。", vm.AttachmentError);
    }

    [Fact]
    public void 添付を外すとPendingAttachmentsに反映される()
    {
        var attachment = new EventAttachment(
            "file1", "https://drive.google.com/file/d/file1/view", "資料.pdf", "application/pdf");

        var source = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24), CalendarId = "primary",
            GoogleEventId = "g1", GoogleCalendarId = "primary", Source = "google",
            GoogleRaw = $$"""
                {"id":"g1","attachments":[
                    {"fileId":"file1","fileUrl":"https://drive.google.com/file/d/file1/view","title":"資料.pdf"}
                ]}
                """,
        };

        var vm = new EventEditorViewModel(source, GoogleCalendars);
        Assert.Single(vm.Attachments);

        vm.RemoveAttachment(attachment);

        Assert.Empty(vm.Attachments);

        var model = vm.ToModel();
        Assert.Equal("[]", model.PendingAttachments);
    }

    [Fact]
    public void 触っていない添付は保存してもPendingAttachmentsのままにしない()
    {
        // GoogleRaw から読んだだけで、足す・外すをしていない予定
        var source = new CalendarEvent
        {
            Id = "e1", Title = "会議", Date = D(2026, 9, 24),
            GoogleEventId = "g1", GoogleCalendarId = "primary", Source = "google",
            GoogleRaw = """
                {"id":"g1","attachments":[
                    {"fileId":"file1","fileUrl":"https://drive.google.com/file/d/file1/view","title":"資料.pdf"}
                ]}
                """,
        };

        var vm = new EventEditorViewModel(source, GoogleCalendars);
        vm.Title = "会議（変更）";

        var model = vm.ToModel();

        // attachments キーを送らせないために、触っていなければ null のまま
        Assert.Null(model.PendingAttachments);
    }

    [Fact]
    public void 添付のURLはhttpsだけ開いてよい()
    {
        var https = new EventAttachment("a", "https://drive.google.com/file/d/a/view", "資料", null);
        var http = new EventAttachment("b", "http://example.com/a", "資料", null);

        Assert.True(EventEditorViewModel.IsSafeToOpen(https));
        Assert.False(EventEditorViewModel.IsSafeToOpen(http));
    }

    /// <summary>アップロードの代わり。あらかじめ決めた結果を返す。</summary>
    private sealed class FakeAttachmentUploader : IAttachmentUploader
    {
        public AttachmentUploadResult Result { get; set; } = AttachmentUploadResult.Failure("未設定");

        public Task<AttachmentUploadResult> UploadAsync(
            string localFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }
}
