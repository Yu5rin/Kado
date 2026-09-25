using Kado.Data.Models;
using Kado.Google.Mapping;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Editing;

/// <summary>終了時刻の候補1つ。長さを添えて、何時間の予定になるかを選ぶ前に見せる。</summary>
/// <param name="Time">「10:30」。</param>
/// <param name="Label">「10:30（1時間30分）」。</param>
public sealed record EndTimeOption(string Time, string Label)
{
    /// <summary>表示名をそのまま返す。理由は <see cref="RecurrenceOption.ToString"/> と同じ。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 予定の編集内容。
/// <para>
/// 項目は Google Calendar のイベントに合わせてある（タイトル＝summary、説明＝description、
/// 場所＝location、URL＝source.url、繰り返し＝recurrence、カレンダー＝calendarId）。
/// Phase 4 で同期を始めたときに、こちらにしか無い項目・あちらにしか無い項目が
/// 出ないようにするため。
/// </para>
/// <para>
/// <b>色は置かない。</b>所属カレンダーで決まるので1件ずつは選ばせない。
/// </para>
/// <para>
/// 時刻は文字列で受ける。「9」「930」「9:30」のどれでも読むので（<see cref="TimeInput"/>）、
/// 打っても選んでも入る。
/// </para>
/// </summary>
public sealed class EventEditorViewModel : ObservableObject
{
    /// <summary>終了時刻の候補をどこまで出すか。半日ぶんあれば足りる。</summary>
    private const int EndChoiceCount = 48;

    /// <summary>
    /// タイトル（Google Calendar の <c>summary</c>）の上限。
    /// <para>
    /// Google Calendar API のリファレンスには、この項目の文字数上限が明記されていない
    /// （2026年9月時点で確認）。<b>確認できなかったので、保守的な値として置いた。</b>
    /// Google Tasks の <c>title</c> の公式な上限（1024文字）に倣っている。
    /// </para>
    /// </summary>
    private const int TitleMaxLength = 1024;

    /// <summary>
    /// 説明（Google Calendar の <c>description</c>）の上限。
    /// <para>
    /// 同上の理由で、Google Tasks の <c>notes</c> の公式な上限（8192文字）に倣った
    /// 保守的な値。実際の Calendar 側の上限を確認できていない。
    /// </para>
    /// </summary>
    private const int NoteMaxLength = 8192;

    /// <summary>添付の上限。Google Calendar の仕様どおり。</summary>
    private const int MaxAttachments = 25;

    private readonly CalendarEvent? _original;
    private readonly IAttachmentUploader _uploader;
    private readonly IFileDialogs _dialogs;

    private List<EventAttachment> _attachments = [];
    private bool _attachmentsDirty;
    private bool _isUploadingAttachment;
    private string? _attachmentError;

    private string _title = string.Empty;
    private DateOnly _date;
    private bool _isAllDay;
    private bool? _notify;
    private string _startTimeText = "09:00";
    private string _endTimeText = "10:00";
    private DateOnly _endDate;
    private string? _location;
    private string? _note;
    private string? _calendarId;
    private string? _url;
    private RecurrenceKind _recurrence;

    /// <summary>
    /// 新しく作る。
    /// <para>
    /// 開始は<b>いまの時刻の次の30分区切り</b>、終了はその1時間後。固定の 9:00 だと、
    /// いつ足してもまず時刻を直すことになる。
    /// </para>
    /// </summary>
    /// <param name="now">いまの時刻。渡さなければ 9:00〜10:00 のまま。</param>
    /// <param name="date">開く日。</param>
    /// <param name="calendars">選べるカレンダー。</param>
    /// <param name="now">いまの時刻。開始時刻の初期値に使う。</param>
    /// <param name="defaultCalendarId">入れ先の既定。左の一覧で選ばれているもの。</param>
    public EventEditorViewModel(DateOnly date, IReadOnlyList<SourceChoice> calendars, TimeOnly? now = null,
        string? defaultCalendarId = null, IAttachmentUploader? uploader = null, IFileDialogs? dialogs = null)
    {
        Calendars = calendars;
        _uploader = uploader ?? NullAttachmentUploader.Instance;
        _dialogs = dialogs ?? NullFileDialogs.Instance;
        _date = date;
        _endDate = date;
        _calendarId = calendars.Any(c => string.Equals(c.Id, defaultCalendarId, StringComparison.Ordinal))
            ? defaultCalendarId
            : calendars.Count > 0 ? calendars[0].Id : null;

        if (now is { } value)
        {
            var start = TimeInput.NextHalfHour(value);

            _startTimeText = TimeInput.Format(start);
            _endTimeText = TimeInput.Format(TimeInput.OneHourAfter(start));
        }
    }

    /// <summary>すでにある予定を直す。</summary>
    public EventEditorViewModel(CalendarEvent value, IReadOnlyList<SourceChoice> calendars,
        IAttachmentUploader? uploader = null, IFileDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        _original = value;
        Calendars = calendars;
        _uploader = uploader ?? NullAttachmentUploader.Instance;
        _dialogs = dialogs ?? NullFileDialogs.Instance;

        _title = value.Title;
        _date = value.Date;
        _isAllDay = value.IsAllDay;
        _location = value.Location;
        _note = value.Note;
        _calendarId = value.CalendarId;
        _url = value.Url;
        _recurrence = RecurrenceChoice.KindOf(value.Recurrence, value.Date);

        if (value.StartTime is { } start) _startTimeText = TimeInput.Format(start);
        if (value.EndTime is { } end) _endTimeText = TimeInput.Format(end);

        _endDate = value.EndDate ?? value.Date;
        _notify = value.Notify;

        // Google Tasks には添付の API が無いので予定だけが対象。ローカルだけのカレンダー
        // の予定は attachments を書き戻せないので使わせない（CanUseAttachments を見よ）
        _attachments = [.. EventMapper.EffectiveAttachments(value)];
    }

    /// <summary>新規か。見出しとボタンの文言を変える。</summary>
    public bool IsNew => _original is null;

    /// <summary>画面の見出し。</summary>
    public string HeaderText => IsNew ? "予定の追加" : "予定の編集";

    /// <summary>
    /// 削除を求めて閉じたか。
    /// <para>
    /// 呼び出し側（<see cref="IEditorPresenter"/> の実装）が画面を閉じたあとにこれを見て、
    /// 実際の削除（確認ダイアログを含む）を行う。編集画面そのものは削除を実行しない。
    /// </para>
    /// </summary>
    public bool Deleted { get; private set; }

    /// <summary>
    /// 削除して閉じることを求める。既存の予定を編集しているときだけ効く。
    /// <para>新規作成の途中では消すものが無いので、呼んでも何もしない。</para>
    /// </summary>
    public void RequestDelete()
    {
        if (IsNew) return;

        Deleted = true;
    }

    /// <summary>選べるカレンダー。ひとつも無ければ欄を出さない。</summary>
    public IReadOnlyList<SourceChoice> Calendars { get; }

    public string Title
    {
        get => _title;
        set => SetAndRevalidate(ref _title, value ?? string.Empty);
    }

    public DateOnly Date
    {
        get => _date;
        set
        {
            var span = _endDate.DayNumber - _date.DayNumber;

            if (!SetAndRevalidate(ref _date, value)) return;

            // 終了日が開始日より前に取り残されるのを防ぐ
            if (_endDate < _date) EndDate = _date;

            // またがる日数を保ったまま、終了日も動かす。
            // 置いていくと「終わりが始まりより前」になる
            if (span > 0) EndDate = _date.AddDays(span);
            else if (_endDate < _date) EndDate = _date;

            // 「毎週 木曜日」は開始日で決まる。日付を動かしたら表示も付いてくる
            Raise(nameof(RecurrenceOptions), nameof(IsMultiDay));
        }
    }

    /// <summary>
    /// この予定を知らせるか。
    /// <para>
    /// 「カレンダーに従う」「知らせる」「知らせない」の3つ。既定は従う。
    /// ふつうはカレンダー側（左パネルのベル）で決め、例外だけここで指す。
    /// </para>
    /// <para>
    /// <b>「カレンダーに従う」はどちらになるかを添えて出す。</b>左パネルのベルを
    /// 開かないと結果が分からず、選んでいるカレンダーによって変わる（実機の報告）。
    /// <see cref="CalendarId"/> を選び直すたびに知らせて追従させる。
    /// </para>
    /// </summary>
    public IReadOnlyList<NotifyChoice> NotifyOptions =>
    [
        new(null, $"カレンダーに従う（{FollowsCalendarLabel}）"),
        new(true, "知らせる"),
        new(false, "知らせない"),
    ];

    /// <summary>
    /// 「カレンダーに従う」を選んだとき、実際に知らせるかどうか。
    /// <para>
    /// いま選んでいるカレンダー（<see cref="CalendarId"/>）の既定に従う。
    /// 一覧に見つからなければ（読み込み中など）、カレンダー側の既定と同じ
    /// 「知らせる」に合わせておく。
    /// </para>
    /// </summary>
    private string FollowsCalendarLabel =>
        Calendars.FirstOrDefault(c => string.Equals(c.Id, _calendarId, StringComparison.Ordinal)) is { } chosen
            ? (chosen.Notifies ? "知らせる" : "知らせない")
            : "知らせる";

    /// <summary>選ばれている通知の指定。</summary>
    public bool? Notify
    {
        get => _notify;
        set => Set(ref _notify, value);
    }

    /// <summary>終日か。時刻欄を使うかどうかが変わる。</summary>
    public bool IsAllDay
    {
        get => _isAllDay;
        set => SetAndRevalidate(ref _isAllDay, value);
    }

    /// <summary>
    /// 開始時刻。「9」「930」「9:30」のどれでも読む。
    /// <para>動かすと終了時刻も同じ長さを保ったまま付いてくる。</para>
    /// </summary>
    public string StartTimeText
    {
        get => _startTimeText;
        set
        {
            var before = TimeInput.Parse(_startTimeText);
            if (!SetAndRevalidate(ref _startTimeText, Tidy(value))) return;

            ShiftEnd(before);
            Raise(nameof(EndTimeOptions), nameof(DurationText));
        }
    }

    /// <summary>終了時刻。候補から選んだ「10:30（1時間30分）」もそのまま読む。</summary>
    public string EndTimeText
    {
        get => _endTimeText;
        set
        {
            if (SetAndRevalidate(ref _endTimeText, Tidy(value))) Raise(nameof(DurationText));
        }
    }

    /// <summary>時刻の候補。15分刻み。</summary>
    public IReadOnlyList<string> StartTimeOptions { get; } = TimeInput.EveryQuarterHour();

    /// <summary>
    /// 終了時刻の候補。開始時刻の後ろだけを、長さを添えて並べる。
    /// <para>「何時まで」より「何時間の予定か」で決めることが多い。</para>
    /// </summary>
    public IReadOnlyList<EndTimeOption> EndTimeOptions
    {
        get
        {
            if (TimeInput.Parse(_startTimeText) is not { } start) return [];

            var options = new List<EndTimeOption>(EndChoiceCount);
            for (var i = 1; i <= EndChoiceCount; i++)
            {
                var candidate = start.AddMinutes(15 * i);

                // 日をまたぐぶんは終了日のほうで表す。候補には出さない
                if (candidate <= start) break;

                var text = TimeInput.Format(candidate);
                options.Add(new EndTimeOption(
                    text, $"{text}（{TimeInput.FormatDuration(start, candidate)}）"));
            }

            return options;
        }
    }

    /// <summary>「1時間30分」。いまの入力での長さ。読めなければ null。</summary>
    public string? DurationText =>
        TimeInput.Parse(_startTimeText) is { } start && TimeInput.Parse(_endTimeText) is { } end
            ? TimeInput.FormatDuration(start, end)
            : null;

    /// <summary>複数日にまたがるか。</summary>
    /// <summary>
    /// 2日以上にまたがるか。
    /// <para>
    /// <b>終了日そのものが決める。</b>始まりと同じ日なら1日、後ろなら複数日。
    /// チェックを入れさせてから終了日を出す作りだと、ひと手間多い。
    /// </para>
    /// </summary>
    public bool IsMultiDay => _endDate > _date;

    /// <summary>
    /// 終了日。
    /// <para>
    /// 始まりより前には置けない。前の日を選んだら、始まりと同じ日に直す。
    /// 「終わりが始まりより前」という保存できない状態を作らせない。
    /// </para>
    /// </summary>
    public DateOnly EndDate
    {
        get => _endDate;
        set
        {
            var end = value < _date ? _date : value;
            if (!SetAndRevalidate(ref _endDate, end)) return;

            Raise(nameof(IsMultiDay), nameof(DurationText));
        }
    }

    /// <summary>繰り返し。Google Calendar の <c>recurrence</c> にあたる。</summary>
    public RecurrenceKind Recurrence
    {
        get => _recurrence;
        set => Set(ref _recurrence, value);
    }

    /// <summary>
    /// 繰り返しの選択肢。開始日に合わせて文言が変わる。
    /// <para>
    /// こちらの5択に当てはまらない指定は「このまま」の1項目になる。その文言には、
    /// 元の指定を読んだ中身（「2週ごと 月・水・金」など）を書く。以前は
    /// 「この予定の設定のまま」としか出ず、繰り返しなのかどうかも読めなかった。
    /// </para>
    /// </summary>
    public IReadOnlyList<RecurrenceOption> RecurrenceOptions =>
        RecurrenceChoice.OptionsFor(
            _date, includeCustom: _recurrence == RecurrenceKind.Custom, spec: _original?.Recurrence);

    /// <summary>場所。Google Calendar の <c>location</c>。</summary>
    public string? Location
    {
        get => _location;
        set => Set(ref _location, value);
    }

    /// <summary>説明。Google Calendar の <c>description</c>。</summary>
    public string? Note
    {
        get => _note;
        // 長さの上限に引っかかるかで保存できるかが変わるので、出し直す
        set => SetAndRevalidate(ref _note, value);
    }

    public string? CalendarId
    {
        get => _calendarId;
        set
        {
            if (!CanChangeCalendar) return;
            if (!Set(ref _calendarId, value)) return;

            // 「カレンダーに従う」の結果はカレンダーごとに変わる。選び直したら文言も追従させる
            // 添付が使えるかも、入れ先が Google 連携かどうかで変わる
            Raise(nameof(NotifyOptions), nameof(CanUseAttachments), nameof(AttachmentsDisabledReason));
        }
    }

    /// <summary>
    /// カレンダー欄を変えられるか。
    /// <para>
    /// 繰り返しのうち1回だけを差し替えた回（例外回）は、Google でも単体で
    /// カレンダーを移せない。編集画面でも変えさせず、理由を
    /// <see cref="CalendarLockReason"/> に出す。
    /// </para>
    /// </summary>
    public bool CanChangeCalendar => _original is null || !EventMapper.IsRecurringInstance(_original);

    /// <summary>カレンダー欄を変えられない理由。変えられるなら null。</summary>
    public string? CalendarLockReason => CanChangeCalendar
        ? null
        : "繰り返しの1回だけを差し替えた予定は、カレンダーを移せません（Google 側の制約）";

    // ------------------------------------------------------------------
    // 添付。Google 連携のカレンダーの予定だけが対象（Google Tasks には無い機能）
    // ------------------------------------------------------------------

    /// <summary>いま付いている添付。</summary>
    public IReadOnlyList<EventAttachment> Attachments => _attachments;

    /// <summary>
    /// 添付を使えるか。
    /// <para>
    /// ローカルだけのカレンダーの予定は Google に書き戻せないので使わせない。
    /// 入れ先を選んでいなければ（一覧が空など）使わせない。
    /// </para>
    /// </summary>
    public bool CanUseAttachments =>
        _calendarId is { Length: > 0 } id && !Kado.Presentation.CalendarWorkspace.IsLocalId(id);

    /// <summary>添付欄を使えない理由。使えるなら null。</summary>
    public string? AttachmentsDisabledReason => CanUseAttachments
        ? null
        : "ローカルだけのカレンダーの予定には添付を付けられません";

    /// <summary>アップロード中か。ボタンの二重押しを防ぐ表示に使う。</summary>
    public bool IsUploadingAttachment
    {
        get => _isUploadingAttachment;
        private set => Set(ref _isUploadingAttachment, value);
    }

    /// <summary>添付にまつわる最後のエラー。無ければ null。</summary>
    public string? AttachmentError
    {
        get => _attachmentError;
        private set => Set(ref _attachmentError, value);
    }

    /// <summary>もう1件足せるか。上限（25件）に達していたら false。</summary>
    public bool CanAddAttachment => CanUseAttachments && !_isUploadingAttachment && _attachments.Count < MaxAttachments;

    /// <summary>
    /// ファイルを選ばせて、ドライブへ上げてから添付に足す。
    /// <para>
    /// 上げるところまでこの中で行う（オフラインなら <see cref="AttachmentError"/> に出す）。
    /// 「足した・外した」という操作をしたときだけ保存時に <c>attachments</c> を送るため、
    /// ここで <see cref="_attachmentsDirty"/> を立てる。
    /// </para>
    /// </summary>
    public async Task AddAttachmentAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAddAttachment) return;

        var path = _dialogs.PickOpenFile("添付するファイル", "すべてのファイル (*.*)|*.*");
        if (path is not { Length: > 0 }) return;

        IsUploadingAttachment = true;
        AttachmentError = null;

        try
        {
            var result = await _uploader.UploadAsync(path, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                AttachmentError = result.ErrorMessage ?? "添付を上げられませんでした。";
                return;
            }

            _attachments = [.. _attachments, result.Attachment!];
            _attachmentsDirty = true;
            Raise(nameof(Attachments), nameof(CanAddAttachment));
        }
        finally
        {
            IsUploadingAttachment = false;
            Raise(nameof(CanAddAttachment));
        }
    }

    /// <summary>
    /// 添付を予定から外す。
    /// <para>ドライブのファイルそのものは消さない。予定との結びつきを外すだけ。</para>
    /// </summary>
    public void RemoveAttachment(EventAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (!CanUseAttachments) return;
        if (!_attachments.Any(a => string.Equals(a.FileId, attachment.FileId, StringComparison.Ordinal))) return;

        _attachments = _attachments
            .Where(a => !string.Equals(a.FileId, attachment.FileId, StringComparison.Ordinal))
            .ToList();
        _attachmentsDirty = true;

        Raise(nameof(Attachments), nameof(CanAddAttachment));
    }

    /// <summary>
    /// 開いてよい URL か。
    /// <para>https 以外は開かない（安全のため）。呼び出し側（画面）はこれが true のときだけ
    /// 既定のブラウザを開く。</para>
    /// </summary>
    public static bool IsSafeToOpen(EventAttachment attachment) =>
        attachment.FileUrl.StartsWith("https://", StringComparison.Ordinal);

    /// <summary>
    /// 添付を既定のブラウザで開く。
    /// <para>
    /// https 以外は開かない。<see cref="SettingsViewModel"/> の「Kado のページを開く」と
    /// 同じ流儀（ShellExecute に無検証で文字列を渡さない）。
    /// </para>
    /// </summary>
    public void OpenAttachment(EventAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (!IsSafeToOpen(attachment))
        {
            AttachmentError = "この添付は開けません（https の URL ではありません）";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(attachment.FileUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or IOException or PlatformNotSupportedException)
        {
            AttachmentError = $"開けませんでした（{ex.Message}）";
        }
    }

    /// <summary>
    /// 関連する URL。Google Calendar の <c>source.url</c>。
    /// <para>資料や図面の置き場所。説明欄に書くと本文と混ざって拾いにくい。</para>
    /// </summary>
    public string? Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    /// <summary>保存できるか。</summary>
    public bool CanSave => ValidationMessage is null;

    /// <summary>
    /// 保存できない理由。問題が無ければ null。
    /// <para>ボタンを押せなくするだけだと、なぜ押せないのか分からない。</para>
    /// </summary>
    public string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_title)) return "タイトルを入れてください。";

            // 長すぎるタイトルは Google 側で断られる。入口で止めておく
            if (_title.Length > TitleMaxLength) return $"タイトルは{TitleMaxLength}文字以内にしてください。";

            if (_note is { Length: > 0 } && _note.Length > NoteMaxLength)
                return $"説明は{NoteMaxLength}文字以内にしてください。";

            if (!_isAllDay)
            {
                if (TimeInput.Parse(_startTimeText) is not { } start) return "開始時刻を「9:30」の形で入れてください。";
                if (TimeInput.Parse(_endTimeText) is not { } end) return "終了時刻を「9:30」の形で入れてください。";

                // 日をまたぐ予定は終了日のほうで表す。時刻の逆転は打ち間違いとみなす
                if (!IsMultiDay && end <= start) return "終了時刻は開始時刻より後にしてください。";
            }

            // 終了日は入れるときに始まりへ寄せてあるので、ここに来ることはない。
            // 別の道から入った値の取りこぼしを拾うための番人として残す
            if (_endDate < _date) return "終了日は開始日以降にしてください。";

            return null;
        }
    }

    /// <summary>
    /// 開始・終了時刻を分単位で動かす。
    /// <para>上下キーやホイールで刻む。1文字ずつ打ち直すより速い。</para>
    /// </summary>
    public void NudgeStart(int minutes) =>
        StartTimeText = TimeInput.Format((TimeInput.Parse(_startTimeText) ?? new TimeOnly(9, 0)).AddMinutes(minutes));

    public void NudgeEnd(int minutes) =>
        EndTimeText = TimeInput.Format((TimeInput.Parse(_endTimeText) ?? new TimeOnly(10, 0)).AddMinutes(minutes));

    /// <summary>入力から予定を組み立てる。<see cref="CanSave"/> が true のときだけ呼ぶ。</summary>
    public CalendarEvent ToModel()
    {
        if (ValidationMessage is { } message) throw new InvalidOperationException(message);

        return new CalendarEvent
        {
            // 既存を直すときは識別子を引き継ぐ。変えると別の予定になってしまう
            Id = _original?.Id ?? NewId(),
            Title = _title.Trim(),
            Date = _date,
            EndDate = _endDate > _date ? _endDate : null,
            StartTime = _isAllDay ? null : TimeInput.Parse(_startTimeText),
            EndTime = _isAllDay ? null : TimeInput.Parse(_endTimeText),
            Location = Blank(_location),
            Note = Blank(_note),
            Url = Blank(_url),
            CalendarId = _calendarId,
            Notify = _notify,

            // 色は所属カレンダーで決まる。1件ずつは選ばせない。
            // 取り込んだ予定が持っている色は、上書きせずそのまま残す
            Color = _original?.Color,

            // 選択肢で表せない指定は、元の文字列をそのまま持ち続ける
            Recurrence = _recurrence == RecurrenceKind.Custom
                ? _original?.Recurrence
                : RecurrenceChoice.ToSpec(_recurrence, _date),

            // Google 側の情報は編集画面で触らない。消さずに引き継ぐ。
            //
            // GoogleRaw を引き継ぎ忘れると、保存するたびに「Google から一度も
            // 受け取っていない」状態に戻ってしまう。EventMapper.NeedsPush は
            // 毎回送り直すようになるだけで済むが、EventMapper.HoldsUnrepresentableRecurrence
            // は GoogleRaw が無いと「表せない繰り返しではない」と誤判定し、
            // カスタムの繰り返し（RDATE など）を持つ予定のタイトルを直しただけで
            // 繰り返しの指定ごと消して送ってしまう。GoogleCalendarId を引き継がないと、
            // カレンダーを移したあと別の項目も直したときに move ではなく素の patch に
            // なり、相手の元のカレンダーに孤立した予定を残してしまう
            GoogleEventId = _original?.GoogleEventId,
            GoogleCalendarId = _original?.GoogleCalendarId,
            GoogleUpdated = _original?.GoogleUpdated,
            GoogleRaw = _original?.GoogleRaw,
            Status = _original?.Status,
            Source = _original?.Source,

            // 添付は「足した・外した」という操作をしたときだけ保存する。触っていなければ
            // 前の値（null なら null のまま）を引き継ぐ
            PendingAttachments = _attachmentsDirty
                ? EventMapper.ToPendingAttachmentsJson(_attachments)
                : _original?.PendingAttachments,

            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// 読めた入力は「09:30」の形に揃える。
    /// <para>
    /// 候補から選ぶと「10:30（1時間30分）」が入るので、長さの添え字を落とす。
    /// 読めない入力はそのまま残す。打ちかけを消されると直せない。
    /// </para>
    /// </summary>
    private static string Tidy(string? value) =>
        TimeInput.Parse(value) is { } time ? TimeInput.Format(time) : value ?? string.Empty;

    /// <summary>開始時刻が動いたぶん、終了時刻も動かして長さを保つ。</summary>
    private void ShiftEnd(TimeOnly? before)
    {
        if (before is not { } previous ||
            TimeInput.Parse(_startTimeText) is not { } start ||
            TimeInput.Parse(_endTimeText) is not { } end) return;

        // 元から逆転していたら触らない。直そうとしている最中かもしれない
        if (end <= previous) return;

        var length = end - previous;
        EndTimeText = TimeInput.Format(start.Add(length));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NewId() => Guid.NewGuid().ToString("N")[..15];

    /// <summary>値を差し替えて、保存できるかどうかも出し直す。</summary>
    private bool SetAndRevalidate<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName)) return false;

        Raise(nameof(CanSave), nameof(ValidationMessage));
        return true;
    }
}
