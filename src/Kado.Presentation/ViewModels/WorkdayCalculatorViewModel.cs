using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Kado.Core.WorkingDays;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Settings;

namespace Kado.Presentation.ViewModels;

/// <summary>
/// 実働日計算パネル（要件書 4.5）。
/// <para>
/// 3つ。期間から実働日数を出すの、基準日から N 実働日進んだ日を出すの、そして
/// 納期から複数の節目を逆算する「工程逆算」（項目1）。常設はしない。ボタンで開いて、
/// 使ったら閉じる。
/// </para>
/// <para>
/// <b>ここは設定の「暦日で数える」に従わない。</b>暦日で数えるなら、この道具そのものが
/// 要らない。実働日で数えたいときに開くものなので、常に実働日で数える（要件書 4.5）。
/// </para>
/// </summary>
public sealed class WorkdayCalculatorViewModel : ObservableObject
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    private readonly WorkingDayMath _math;
    private readonly AppSettings? _settings;

    private DateOnly _rangeFrom;
    private DateOnly _rangeTo;
    private DateOnly _baseDate;
    private int _offset = 10;

    public WorkdayCalculatorViewModel(WorkingDayMath math, DateOnly today, AppSettings? settings = null)
    {
        _math = math ?? throw new ArgumentNullException(nameof(math));
        _settings = settings;

        _rangeFrom = today;
        _rangeTo = today;
        _baseDate = today;
        _planDueDate = today;

        Plans = new ObservableCollection<WorkdayPlanEditRow>(
            (_settings?.WorkdayOffsetPlans ?? [WorkdayOffsetPlan.CreateDefault()])
            .Select(p => new WorkdayPlanEditRow(p)));

        foreach (var plan in Plans) HookPlan(plan);

        _selectedPlan = Plans.FirstOrDefault(p => p.Id == _settings?.SelectedWorkdayOffsetPlanId)
            ?? Plans.FirstOrDefault();

        AddPlanCommand = new RelayCommand(AddPlan);
        RemovePlanCommand = new RelayCommand(RemoveSelectedPlan, () => SelectedPlan is not null);
        AddStepCommand = new RelayCommand(AddStep, () => SelectedPlan is not null);
        RemoveStepCommand = new RelayCommand<WorkdayStepEditRow>(RemoveStep);
        MoveStepUpCommand = new RelayCommand<WorkdayStepEditRow>(row => MoveStep(row, -1));
        MoveStepDownCommand = new RelayCommand<WorkdayStepEditRow>(row => MoveStep(row, 1));
        CreateEventFromPlanCommand = new RelayCommand<WorkdayPlanResultRow>(
            row => { if (row?.Date is { } date) CreateEventAt?.Invoke(date); });
        CreateTaskFromPlanCommand = new RelayCommand<WorkdayPlanResultRow>(
            row => { if (row?.Date is { } date) CreateTaskAt?.Invoke(date); });
        CopyPlanResultCommand = new RelayCommand(CopyPlanResult, () => PlanRows.Count > 0);

        RecomputePlanRows();
    }

    /// <summary>実働日データを持っているか。無ければ出しても何も出せない。</summary>
    public bool HasData => _math.Calendar.Count > 0;

    /// <summary>取り込んである範囲。「2026/4/1 〜 2027/3/31」。無ければ null。</summary>
    public string? CoverageText =>
        _math.Calendar is { RangeStart: { } start, RangeEnd: { } end }
            ? $"{start.ToString("yyyy/M/d", CultureInfo.InvariantCulture)} 〜 " +
              $"{end.ToString("yyyy/M/d", CultureInfo.InvariantCulture)}"
            : null;

    // ------------------------------------------------------------------
    // 期間 → 実働日数
    // ------------------------------------------------------------------

    /// <summary>数え始める日。</summary>
    public DateOnly RangeFrom
    {
        get => _rangeFrom;
        set
        {
            if (!Set(ref _rangeFrom, value)) return;

            // 逆に入れられたら、終わりを始まりに寄せる。負の日数を出しても読めない
            if (_rangeTo < _rangeFrom) Set(ref _rangeTo, _rangeFrom, nameof(RangeTo));

            RaiseRange();
        }
    }

    /// <summary>数え終わる日。この日を含む。</summary>
    public DateOnly RangeTo
    {
        get => _rangeTo;
        set
        {
            if (!Set(ref _rangeTo, value)) return;

            if (_rangeTo < _rangeFrom) Set(ref _rangeFrom, _rangeTo, nameof(RangeFrom));

            RaiseRange();
        }
    }

    /// <summary>
    /// 期間の実働日数。両端を含む。
    /// <para>
    /// <see cref="WorkingDayMath.CountBetween"/> は始まりを含まない（後ろ側だけ数える）。
    /// 人が「4/1 から 4/10 まで何日働くか」と聞くときは<b>両端を含む</b>ので、
    /// 始まりが稼働日なら1足す。
    /// </para>
    /// </summary>
    public int? RangeCount
    {
        get
        {
            if (_math.CountBetween(_rangeFrom, _rangeTo) is not { } exclusive) return null;

            return exclusive + (_math.Calendar.IsWorkingDay(_rangeFrom) ? 1 : 0);
        }
    }

    /// <summary>期間の暦日数。両端を含む。</summary>
    public int RangeCalendarDays => _rangeTo.DayNumber - _rangeFrom.DayNumber + 1;

    /// <summary>「12 実働日」。範囲の外なら断り書き。</summary>
    public string RangeResultText => RangeCount is { } count
        ? $"{count} 実働日"
        : "実働日データの範囲外です";

    /// <summary>「暦日では 14 日（うち休業 2 日）」。</summary>
    public string RangeDetailText
    {
        get
        {
            var calendar = RangeCalendarDays;

            if (RangeCount is not { } count) return $"暦日では {calendar} 日";

            return $"暦日では {calendar} 日（うち休業 {calendar - count} 日）";
        }
    }

    /// <summary>数えられたか。出せないときは結果を薄くする。</summary>
    public bool HasRangeResult => RangeCount is not null;

    // ------------------------------------------------------------------
    // 基準日 ＋ N 実働日 → 到達日
    // ------------------------------------------------------------------

    /// <summary>数え始める日。この日自体は数に入れない。</summary>
    public DateOnly BaseDate
    {
        get => _baseDate;
        set
        {
            if (!Set(ref _baseDate, value)) return;

            RaiseOffset();
        }
    }

    /// <summary>進める実働日数。負にすると遡る。</summary>
    public int Offset
    {
        get => _offset;
        set
        {
            if (!Set(ref _offset, value)) return;

            RaiseOffset();
        }
    }

    /// <summary>到達日。データが尽きたら null。</summary>
    public DateOnly? Arrival => _math.AddWorkingDays(_baseDate, _offset);

    /// <summary>「2026年10月9日（金）」。出せなければ断り書き。</summary>
    public string ArrivalResultText => Arrival is { } date
        ? date.ToString("yyyy年M月d日（ddd）", Japanese)
        : "実働日データが足りません";

    /// <summary>「基準日から暦日で 15 日後」。出せなければ null。</summary>
    public string? ArrivalDetailText
    {
        get
        {
            if (Arrival is not { } date) return null;

            var days = date.DayNumber - _baseDate.DayNumber;

            return days switch
            {
                0 => "基準日そのもの",
                > 0 => $"基準日から暦日で {days} 日後",
                _ => $"基準日から暦日で {-days} 日前",
            };
        }
    }

    /// <summary>出せたか。</summary>
    public bool HasArrival => Arrival is not null;

    /// <summary>
    /// カレンダー上でドラッグして選んだ期間を入れる（要件書 4.5）。
    /// <para>掴んだ向きは問わない。逆向きでも同じ期間として受ける。</para>
    /// </summary>
    public void SetRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);

        Set(ref _rangeFrom, from, nameof(RangeFrom));
        Set(ref _rangeTo, to, nameof(RangeTo));
        RaiseRange();
    }

    /// <summary>
    /// ウィンドウが閉じられたときに上がる。
    /// <para>
    /// モードレスで出すようになったので、開いているあいだだけカレンダー上の
    /// クリックをこちらへ流したい（<c>MainViewModel</c> 側の役目）。閉じたことを
    /// 知る手立てがここにしか無いので、窓を持つ側（<c>DialogEditorPresenter</c>）が
    /// <see cref="NotifyClosed"/> を呼んで伝える。
    /// </para>
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>窓を持つ側から、閉じられたことを伝える。</summary>
    public void NotifyClosed() => Closed?.Invoke(this, EventArgs.Empty);

    private void RaiseRange() => Raise(
        nameof(RangeCount), nameof(RangeCalendarDays),
        nameof(RangeResultText), nameof(RangeDetailText), nameof(HasRangeResult));

    private void RaiseOffset() => Raise(
        nameof(Arrival), nameof(ArrivalResultText), nameof(ArrivalDetailText), nameof(HasArrival));

    // ------------------------------------------------------------------
    // 工程逆算（項目1）: 納期 → 名前付きオフセット列 → 各節目の到達日
    // ------------------------------------------------------------------

    private WorkdayPlanEditRow? _selectedPlan;
    private DateOnly _planDueDate;
    private IReadOnlyList<WorkdayPlanResultRow> _planRows = [];

    /// <summary>
    /// 持っているオフセット列（複数セット）。
    /// <para>
    /// 製品や区分でリードタイム構造が違うことがあるため、複数持てる。編集はここで
    /// 直接する（名前・オフセットの書き換えは自動で保存される）。
    /// </para>
    /// </summary>
    public ObservableCollection<WorkdayPlanEditRow> Plans { get; }

    /// <summary>いま選んでいるセット。無ければ null（1つも無いとき）。</summary>
    public WorkdayPlanEditRow? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!Set(ref _selectedPlan, value)) return;

            if (_settings is not null) _settings.SelectedWorkdayOffsetPlanId = value?.Id;

            Raise(nameof(HasSelectedPlan));
            RemovePlanCommand.RaiseCanExecuteChanged();
            AddStepCommand.RaiseCanExecuteChanged();
            RecomputePlanRows();
        }
    }

    /// <summary>セットを選べているか。「セット削除」「＋節目」を出せるかに使う。</summary>
    public bool HasSelectedPlan => _selectedPlan is not null;

    /// <summary>逆算の基準日（＝納期）。</summary>
    public DateOnly PlanDueDate
    {
        get => _planDueDate;
        set
        {
            if (!Set(ref _planDueDate, value)) return;

            RecomputePlanRows();
        }
    }

    /// <summary>各節目の到達日。選んでいるセットの並び順のまま。</summary>
    public IReadOnlyList<WorkdayPlanResultRow> PlanRows
    {
        get => _planRows;
        private set
        {
            _planRows = value;
            Raise(nameof(PlanRows));
            CopyPlanResultCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// この日に予定を作ってほしいと頼まれたときの窓口。
    /// <para>
    /// ここでは何もしない（<c>MainViewModel</c> が、既存の予定エディタを日付を
    /// 入れた状態で開く処理を代入する）。未配線でもボタンごと隠せるよう、
    /// <see cref="HasCreateEventAction"/> で有無を判定できるようにしてある。
    /// </para>
    /// </summary>
    public Action<DateOnly>? CreateEventAt
    {
        get => _createEventAt;
        set
        {
            _createEventAt = value;
            Raise(nameof(HasCreateEventAction));
        }
    }

    /// <summary>この日にタスクを作ってほしいと頼まれたときの窓口。<see cref="CreateEventAt"/> と同じ扱い。</summary>
    public Action<DateOnly>? CreateTaskAt
    {
        get => _createTaskAt;
        set
        {
            _createTaskAt = value;
            Raise(nameof(HasCreateTaskAction));
        }
    }

    private Action<DateOnly>? _createEventAt;
    private Action<DateOnly>? _createTaskAt;

    /// <summary>「この日に予定を作る」ボタンを出せるか。配線前は隠す。</summary>
    public bool HasCreateEventAction => CreateEventAt is not null;

    /// <summary>「この日にタスクを作る」ボタンを出せるか。配線前は隠す。</summary>
    public bool HasCreateTaskAction => CreateTaskAt is not null;

    /// <summary>
    /// 結果をクリップボードへ渡してほしいときの窓口。
    /// <para>クリップボードは WPF 側の機能なので、ここでは文字列を渡すだけにする
    /// （<c>WorkdayCalculatorWindow.xaml.cs</c> が <c>Clipboard.SetText</c> を代入する）。</para>
    /// </summary>
    public Action<string>? CopyText { get; set; }

    public RelayCommand AddPlanCommand { get; }
    public RelayCommand RemovePlanCommand { get; }
    public RelayCommand AddStepCommand { get; }
    public RelayCommand<WorkdayStepEditRow> RemoveStepCommand { get; }
    public RelayCommand<WorkdayStepEditRow> MoveStepUpCommand { get; }
    public RelayCommand<WorkdayStepEditRow> MoveStepDownCommand { get; }
    public RelayCommand<WorkdayPlanResultRow> CreateEventFromPlanCommand { get; }
    public RelayCommand<WorkdayPlanResultRow> CreateTaskFromPlanCommand { get; }
    public RelayCommand CopyPlanResultCommand { get; }

    private void AddPlan()
    {
        var plan = new WorkdayPlanEditRow(new WorkdayOffsetPlan(
            Guid.NewGuid().ToString("N"), "新しいセット", []));

        HookPlan(plan);
        Plans.Add(plan);
        Persist();

        SelectedPlan = plan;
    }

    private void RemoveSelectedPlan()
    {
        if (SelectedPlan is not { } plan) return;

        UnhookPlan(plan);
        Plans.Remove(plan);
        Persist();

        SelectedPlan = Plans.FirstOrDefault();
    }

    // 節目の追加・削除・並べ替えは Steps（ObservableCollection）をそのまま操作するだけでよい。
    // WorkdayPlanEditRow.Changed が拾って、Persist・RecomputePlanRows を呼ぶ
    // （ItemsControl からの直接編集も同じ経路を通る）

    private void AddStep()
    {
        if (SelectedPlan is not { } plan) return;

        plan.Steps.Add(new WorkdayStepEditRow("新しい節目", 0));
    }

    private void RemoveStep(WorkdayStepEditRow? step)
    {
        if (step is null) return;

        SelectedPlan?.Steps.Remove(step);
    }

    /// <summary>節目の並びを1つ動かす。<paramref name="direction"/> は -1（上へ）か 1（下へ）。</summary>
    private void MoveStep(WorkdayStepEditRow? step, int direction)
    {
        if (step is null || SelectedPlan is not { } plan) return;

        var index = plan.Steps.IndexOf(step);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= plan.Steps.Count) return;

        plan.Steps.Move(index, target);
    }

    private void CopyPlanResult()
    {
        if (CopyText is null || PlanRows.Count == 0) return;

        var lines = new List<string> { "節目\tオフセット\t日付" };
        lines.AddRange(PlanRows.Select(row =>
            $"{row.Name}\t{row.OffsetText}\t"
            + (row.Date is { } date
                ? date.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                : "（実働日データが足りません）")));

        // タブ区切りにしてある。Excel にそのまま貼ると列が分かれる
        CopyText(string.Join(Environment.NewLine, lines));
    }

    private void RecomputePlanRows()
    {
        PlanRows = SelectedPlan?.Steps
            .Select(step =>
            {
                var date = _math.AddWorkingDays(_planDueDate, step.Offset);
                return new WorkdayPlanResultRow(
                    step.Name, step.Offset, date,
                    date is { } d
                        ? d.ToString("yyyy年M月d日（ddd）", Japanese)
                        : "実働日データが足りません");
            })
            .ToArray() ?? [];
    }

    private void Persist()
    {
        if (_settings is null) return;

        _settings.WorkdayOffsetPlans = Plans.Select(p => p.ToModel()).ToArray();
    }

    private void HookPlan(WorkdayPlanEditRow plan) => plan.Changed += OnPlanChanged;

    private void UnhookPlan(WorkdayPlanEditRow plan) => plan.Changed -= OnPlanChanged;

    /// <summary>
    /// セットの名前か、節目（追加・削除・並べ替え・名前・オフセット）のどれかが変わった。
    /// <para>
    /// いま選んでいるセットでなければ、保存はしても結果の並びは組み直さない
    /// （画面には出ていないので、組み直しても意味が無い）。
    /// </para>
    /// </summary>
    private void OnPlanChanged(object? sender, EventArgs e)
    {
        Persist();
        if (ReferenceEquals(sender, SelectedPlan)) RecomputePlanRows();
    }
}

/// <summary>工程逆算の節目1行の編集用。名前とオフセットを書き換えられる。</summary>
public sealed class WorkdayStepEditRow : ObservableObject
{
    private string _name;
    private int _offset;

    public WorkdayStepEditRow(string name, int offset)
    {
        _name = name;
        _offset = offset;
    }

    /// <summary>節目の名前。「仕様期限」「1次GO」など。マイルストーン名に合わせておくと見比べやすい。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>基準日（納期）からの実働日数。負で遡る。</summary>
    public int Offset
    {
        get => _offset;
        set => Set(ref _offset, value);
    }

    public WorkdayOffsetStep ToModel() => new(Name, Offset);
}

/// <summary>工程逆算の1セットの編集用。名前を書き換えられ、節目の列を持つ。</summary>
public sealed class WorkdayPlanEditRow : ObservableObject
{
    private string _name;

    public WorkdayPlanEditRow(WorkdayOffsetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Id = plan.Id;
        _name = plan.Name;
        Steps = new ObservableCollection<WorkdayStepEditRow>(
            plan.Steps.Select(s => new WorkdayStepEditRow(s.Name, s.Offset)));

        // 名前・節目の中身のどちらが変わっても、外（WorkdayCalculatorViewModel）が
        // 保存し直し・再計算できるよう、ここでまとめて1つの Changed に集約する。
        // 「＋節目」などのコマンド経由だけでなく、Steps を直に書き換えても拾えるように
        // しておく（ItemsControl は Steps へ直接束ねているので、経路は1つに限らない）
        PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Steps.CollectionChanged += OnStepsCollectionChanged;
        foreach (var step in Steps) HookStep(step);
    }

    /// <summary>内部キー。画面には出さない。</summary>
    public string Id { get; }

    /// <summary>セットの名前。「量産品」「試作品」など。</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>並び順を保った節目の列。</summary>
    public ObservableCollection<WorkdayStepEditRow> Steps { get; }

    /// <summary>名前・節目のどれかが変わった。保存し直し・再計算のきっかけに使う。</summary>
    public event EventHandler? Changed;

    public WorkdayOffsetPlan ToModel() => new(Id, Name, Steps.Select(s => s.ToModel()).ToArray());

    public override string ToString() => Name;

    private void OnStepsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (WorkdayStepEditRow step in e.OldItems) UnhookStep(step);
        }

        if (e.NewItems is not null)
        {
            foreach (WorkdayStepEditRow step in e.NewItems) HookStep(step);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void HookStep(WorkdayStepEditRow step) => step.PropertyChanged += OnStepPropertyChanged;

    private void UnhookStep(WorkdayStepEditRow step) => step.PropertyChanged -= OnStepPropertyChanged;

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>工程逆算の結果1行（表示専用。編集は <see cref="WorkdayStepEditRow"/> 側で行う）。</summary>
/// <param name="Name">節目の名前。</param>
/// <param name="Offset">基準日からのオフセット。</param>
/// <param name="Date">到達日。実働日データが足りなければ null。</param>
/// <param name="DateText">画面にそのまま出せる文字列。</param>
public sealed record WorkdayPlanResultRow(string Name, int Offset, DateOnly? Date, string DateText)
{
    /// <summary>到達日を出せたか。「予定を作る」「タスクを作る」を押せるかに使う。</summary>
    public bool HasDate => Date is not null;

    /// <summary>「−12」「+3」「0」。符号を必ず付けて遡りか先送りかを分かるようにする。</summary>
    public string OffsetText => Offset switch
    {
        > 0 => $"+{Offset}",
        < 0 => Offset.ToString(CultureInfo.InvariantCulture),
        _ => "0",
    };
}
