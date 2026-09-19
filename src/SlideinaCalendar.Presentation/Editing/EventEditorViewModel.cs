using System.Globalization;
using SlideinaCalendar.Data.Models;
using SlideinaCalendar.Presentation.Infrastructure;
using SlideinaCalendar.Presentation.ViewModels;

namespace SlideinaCalendar.Presentation.Editing;

/// <summary>
/// 予定の編集内容。
/// <para>
/// 時刻は文字列で受ける。WPF に時刻の入力欄が無く、日付欄を2つ並べるより
/// 「9:00」と打てるほうが速い。読めない文字列は保存させず、理由をその場で出す。
/// </para>
/// </summary>
public sealed class EventEditorViewModel : ObservableObject
{
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
    private EventAccent _accent;

    /// <summary>新しく作る。</summary>
    public EventEditorViewModel(DateOnly date, IReadOnlyList<string> calendars)
    {
        Calendars = calendars;
        _date = date;
        _endDate = date;
        _calendarId = calendars.Count > 0 ? calendars[0] : null;
    }

    /// <summary>すでにある予定を直す。</summary>
    public EventEditorViewModel(CalendarEvent value, IReadOnlyList<string> calendars)
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
        _accent = EventChipViewModel.ResolveAccent(value.Color);

        if (value.StartTime is { } start) _startTimeText = Format(start);
        if (value.EndTime is { } end) _endTimeText = Format(end);

        _isMultiDay = value.EndDate is { } endDate && endDate > value.Date;
        _endDate = value.EndDate ?? value.Date;
    }

    /// <summary>新規か。見出しとボタンの文言を変える。</summary>
    public bool IsNew => _original is null;

    /// <summary>画面の見出し。</summary>
    public string HeaderText => IsNew ? "予定の追加" : "予定の編集";

    /// <summary>選べるカレンダー。ひとつも無ければ欄を出さない。</summary>
    public IReadOnlyList<string> Calendars { get; }

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
        }
    }

    /// <summary>終日か。時刻欄を使うかどうかが変わる。</summary>
    public bool IsAllDay
    {
        get => _isAllDay;
        set => SetAndRevalidate(ref _isAllDay, value);
    }

    /// <summary>「09:00」。終日なら使わない。</summary>
    public string StartTimeText
    {
        get => _startTimeText;
        set => SetAndRevalidate(ref _startTimeText, value ?? string.Empty);
    }

    public string EndTimeText
    {
        get => _endTimeText;
        set => SetAndRevalidate(ref _endTimeText, value ?? string.Empty);
    }

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

    public string? Location
    {
        get => _location;
        set => Set(ref _location, value);
    }

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

    /// <summary>帯の色。月ビューのチップと右ペインの縦棒に出る。</summary>
    public EventAccent Accent
    {
        get => _accent;
        set => Set(ref _accent, value);
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
                if (ParseTime(_startTimeText) is not { } start) return "開始時刻は「9:00」の形で入れてください。";
                if (ParseTime(_endTimeText) is not { } end) return "終了時刻は「9:00」の形で入れてください。";

                // 日をまたぐ予定は終了日のほうで表す。時刻の逆転は打ち間違いとみなす
                if (!_isMultiDay && end <= start) return "終了時刻は開始時刻より後にしてください。";
            }

            if (_isMultiDay && _endDate < _date) return "終了日は開始日以降にしてください。";

            return null;
        }
    }

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
            StartTime = _isAllDay ? null : ParseTime(_startTimeText),
            EndTime = _isAllDay ? null : ParseTime(_endTimeText),
            Location = Blank(_location),
            Note = Blank(_note),
            Color = ColorOf(_accent),
            CalendarId = _calendarId,

            // 繰り返しと Google 側の情報は編集画面で触らない。消さずに引き継ぐ
            Recurrence = _original?.Recurrence,
            GoogleEventId = _original?.GoogleEventId,
            GoogleUpdated = _original?.GoogleUpdated,
            Source = _original?.Source,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>入力の形を確かめるためだけに使う。</summary>
    internal static TimeOnly? ParseTime(string text) =>
        TimeOnly.TryParse(text?.Trim(), CultureInfo.InvariantCulture, out var time) ? time : null;

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 系統から保存する色を決める。
    /// <para>
    /// <see cref="EventChipViewModel.ResolveAccent"/> と対になる。読むときと書くときで
    /// 別の対応表を持つと、保存し直すたびに色が変わる。
    /// </para>
    /// </summary>
    internal static string? ColorOf(EventAccent accent) => accent switch
    {
        EventAccent.Green => "#d1fae5",
        EventAccent.Amber => "#fef3c7",
        _ => null,
    };

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
