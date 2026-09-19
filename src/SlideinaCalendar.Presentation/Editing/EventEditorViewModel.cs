using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Editing;

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

    private readonly CalendarEvent? _original;

    private string _title = string.Empty;
    private DateOnly _date;
    private bool _isAllDay;
    private string _startTimeText = "09:00";
    private string _endTimeText = "10:00";
    private bool _isMultiDay;
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
    public EventEditorViewModel(DateOnly date, IReadOnlyList<SourceChoice> calendars, TimeOnly? now = null)
    {
        Calendars = calendars;
        _date = date;
        _endDate = date;
        _calendarId = calendars.Count > 0 ? calendars[0].Id : null;

        if (now is { } value)
        {
            var start = TimeInput.NextHalfHour(value);

            _startTimeText = TimeInput.Format(start);
            _endTimeText = TimeInput.Format(TimeInput.OneHourAfter(start));
        }
    }

    /// <summary>すでにある予定を直す。</summary>
    public EventEditorViewModel(CalendarEvent value, IReadOnlyList<SourceChoice> calendars)
    {
        ArgumentNullException.ThrowIfNull(value);

        _original = value;
        Calendars = calendars;

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

        _isMultiDay = value.EndDate is { } endDate && endDate > value.Date;
        _endDate = value.EndDate ?? value.Date;
    }

    /// <summary>新規か。見出しとボタンの文言を変える。</summary>
    public bool IsNew => _original is null;

    /// <summary>画面の見出し。</summary>
    public string HeaderText => IsNew ? "予定の追加" : "予定の編集";

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
            if (!SetAndRevalidate(ref _date, value)) return;

            // 終了日が開始日より前に取り残されるのを防ぐ
            if (_endDate < _date) EndDate = _date;

            // 「毎週 木曜日」は開始日で決まる。日付を動かしたら表示も付いてくる
            Raise(nameof(RecurrenceOptions));
        }
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
    public bool IsMultiDay
    {
        get => _isMultiDay;
        set => SetAndRevalidate(ref _isMultiDay, value);
    }

    public DateOnly EndDate
    {
        get => _endDate;
        set => SetAndRevalidate(ref _endDate, value);
    }

    /// <summary>繰り返し。Google Calendar の <c>recurrence</c> にあたる。</summary>
    public RecurrenceKind Recurrence
    {
        get => _recurrence;
        set => Set(ref _recurrence, value);
    }

    /// <summary>繰り返しの選択肢。開始日に合わせて文言が変わる。</summary>
    public IReadOnlyList<RecurrenceOption> RecurrenceOptions =>
        RecurrenceChoice.OptionsFor(_date, includeCustom: _recurrence == RecurrenceKind.Custom);

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
        set => Set(ref _note, value);
    }

    public string? CalendarId
    {
        get => _calendarId;
        set => Set(ref _calendarId, value);
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

            if (!_isAllDay)
            {
                if (TimeInput.Parse(_startTimeText) is not { } start) return "開始時刻を「9:30」の形で入れてください。";
                if (TimeInput.Parse(_endTimeText) is not { } end) return "終了時刻を「9:30」の形で入れてください。";

                // 日をまたぐ予定は終了日のほうで表す。時刻の逆転は打ち間違いとみなす
                if (!_isMultiDay && end <= start) return "終了時刻は開始時刻より後にしてください。";
            }

            if (_isMultiDay && _endDate < _date) return "終了日は開始日以降にしてください。";

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
            EndDate = _isMultiDay && _endDate > _date ? _endDate : null,
            StartTime = _isAllDay ? null : TimeInput.Parse(_startTimeText),
            EndTime = _isAllDay ? null : TimeInput.Parse(_endTimeText),
            Location = Blank(_location),
            Note = Blank(_note),
            Url = Blank(_url),
            CalendarId = _calendarId,

            // 色は所属カレンダーで決まる。1件ずつは選ばせない。
            // 取り込んだ予定が持っている色は、上書きせずそのまま残す
            Color = _original?.Color,

            // 選択肢で表せない指定は、元の文字列をそのまま持ち続ける
            Recurrence = _recurrence == RecurrenceKind.Custom
                ? _original?.Recurrence
                : RecurrenceChoice.ToSpec(_recurrence, _date),

            // Google 側の情報は編集画面で触らない。消さずに引き継ぐ
            GoogleEventId = _original?.GoogleEventId,
            GoogleUpdated = _original?.GoogleUpdated,
            Source = _original?.Source,
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
