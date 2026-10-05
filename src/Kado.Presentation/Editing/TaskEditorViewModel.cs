using Kado.Data.Models;
using Kado.Google.Mapping;
using Kado.Presentation.Infrastructure;
using Kado.Presentation.Links;

namespace Kado.Presentation.Editing;

/// <summary>期限の早入れ1つ。</summary>
/// <param name="Label">「明日」。</param>
/// <param name="Date">その日。</param>
public sealed record DuePreset(string Label, DateOnly Date);

/// <summary>
/// タスクの編集内容。
/// <para>
/// 項目は Google Tasks に合わせてある（タイトル＝title、詳細＝notes、期限＝due、
/// 完了＝status）。Google Tasks の期限は日付だけで時刻を持たないので、こちらも
/// 時刻は置かない。
/// </para>
/// <para>
/// 期限は「決まっていない」を持てる（要件書 3.1）。日付欄を空にできない代わりに、
/// 期限を付けるかどうかの切り替えを別に持つ。
/// </para>
/// <para>
/// <b>URL と添付（ファイルの場所）は Kado だけの項目</b>で、Google Tasks には無い。手元にだけ持ち、
/// Google には送らない（<see cref="LocalOnlyNote"/> で、画面にもそう書く）。
/// </para>
/// </summary>
public sealed class TaskEditorViewModel : ObservableObject
{
    /// <summary>
    /// タイトル（Google Tasks の <c>title</c>）の上限。
    /// <para>Google Tasks API の公式リファレンスに明記されている値（1024文字）。</para>
    /// </summary>
    private const int TitleMaxLength = 1024;

    /// <summary>
    /// 詳細（Google Tasks の <c>notes</c>）の上限。
    /// <para>Google Tasks API の公式リファレンスに明記されている値（8192文字）。</para>
    /// </summary>
    private const int NoteMaxLength = 8192;

    /// <summary>ファイル選びの絞り込み。場所を持つだけなので、種類は問わない。</summary>
    private const string AllFilesFilter = "すべてのファイル (*.*)|*.*";

    private readonly TaskItem? _original;
    private readonly DateOnly _today;
    private readonly IFileDialogs _dialogs;
    private readonly LinkLauncher _links;

    /// <summary>手元で、このタスクを親として指しているタスクがあるか（サブタスクを持つ親か）。</summary>
    private readonly bool _hasChildren;

    private string _title = string.Empty;
    private bool _hasDue = true;
    private DateOnly _due;
    private bool _isDone;
    private string? _note;
    private string? _taskListId;
    private string? _url;
    private IReadOnlyList<TaskAttachment> _attachments = [];
    private bool _attachmentsDirty;
    private string? _attachmentMessage;

    /// <summary>
    /// 新しく作る。
    /// <para>
    /// <paramref name="defaultTaskListId"/> は呼び出し側（<c>SourceListsViewModel.DefaultTaskList</c>）
    /// が決めた入れ先。渡さなければ一覧の先頭にするが、これは<b>同期対象外のローカル
    /// リストに固定されうる</b>ので、呼び出し側は極力渡すこと（項目2）。
    /// </para>
    /// </summary>
    /// <param name="due">期限の初期値。</param>
    /// <param name="taskLists">選べるタスクリスト。</param>
    /// <param name="today">今日。</param>
    /// <param name="defaultTaskListId">入れ先の既定。</param>
    /// <param name="dialogs">添付のファイル・フォルダを選ばせる口。</param>
    /// <param name="links">添付の場所を開く口。渡さなければ何も起動しない。</param>
    public TaskEditorViewModel(DateOnly due, IReadOnlyList<SourceChoice> taskLists, DateOnly today,
        string? defaultTaskListId = null, IFileDialogs? dialogs = null, LinkLauncher? links = null)
    {
        _dialogs = dialogs ?? NullFileDialogs.Instance;
        _links = links ?? LinkLauncher.None;
        TaskLists = taskLists;
        _today = today;
        _due = due;
        _taskListId = defaultTaskListId ?? (taskLists.Count > 0 ? taskLists[0].Id : null);
    }

    /// <summary>すでにあるタスクを直す。</summary>
    /// <param name="value">直すタスク。</param>
    /// <param name="taskLists">選べるタスクリスト。</param>
    /// <param name="today">今日。期限なしのタスクに期限を付けるときの初期値。</param>
    /// <param name="hasChildren">
    /// サブタスクを持つ親か。親子のタスクは Google でリストをまたいで移せないので、
    /// リスト欄を変えさせない（<see cref="TaskListLockReason"/>）。
    /// </param>
    /// <param name="dialogs">添付のファイル・フォルダを選ばせる口。</param>
    /// <param name="links">添付の場所を開く口。渡さなければ何も起動しない。</param>
    public TaskEditorViewModel(TaskItem value, IReadOnlyList<SourceChoice> taskLists, DateOnly today,
        bool hasChildren = false, IFileDialogs? dialogs = null, LinkLauncher? links = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        _dialogs = dialogs ?? NullFileDialogs.Instance;
        _links = links ?? LinkLauncher.None;
        _original = value;
        _hasChildren = hasChildren;
        TaskLists = taskLists;
        _today = today;

        _title = value.Title;
        _hasDue = value.HasDue;
        // 期限が無いタスクに付けると決めたときの初期値
        _due = value.Due ?? today;
        _isDone = value.IsDone;
        _note = value.Note;
        _taskListId = value.TaskListId;
        _url = value.Url;
        _attachments = TaskAttachments.Read(value.Attachments);
    }

    /// <summary>期限の早入れ。日付欄を開かずに決められる。</summary>
    public IReadOnlyList<DuePreset> DuePresets =>
    [
        new("今日", _today),
        new("明日", _today.AddDays(1)),
        new("来週", _today.AddDays(7)),
    ];

    public bool IsNew => _original is null;

    /// <summary>
    /// 同期で「Google 上で見つからない」印が付いたタスクか。
    /// <para>理由と扱いは <see cref="EventEditorViewModel.IsMissingOnGoogle"/> と同じ。</para>
    /// </summary>
    public bool IsMissingOnGoogle => _original?.GoogleMissing == true;

    /// <summary>印が付いているときに、編集画面に出す説明。無ければ null。</summary>
    public string? MissingOnGoogleMessage => IsMissingOnGoogle
        ? "Google 上でこのタスクが見つかりません（別のリストへ移された、削除された、など）。" +
          "このままでは変更が Google に送られません。"
        : null;

    /// <summary>「Google に新しく作り直す」を求めて保存したか。</summary>
    public bool RecreateRequested { get; private set; }

    /// <summary>「Google に新しく作り直す」を選ぶ。印が付いたタスクのときだけ効く。</summary>
    public void RequestRecreate()
    {
        if (IsMissingOnGoogle) RecreateRequested = true;
    }

    public string HeaderText => IsNew ? "タスクの追加" : "タスクの編集";

    /// <summary>
    /// 削除を求めて閉じたか。
    /// <para>
    /// 呼び出し側（<see cref="IEditorPresenter"/> の実装）が画面を閉じたあとにこれを見て、
    /// 実際の削除（確認ダイアログを含む）を行う。編集画面そのものは削除を実行しない。
    /// </para>
    /// </summary>
    public bool Deleted { get; private set; }

    /// <summary>
    /// 削除して閉じることを求める。既存のタスクを編集しているときだけ効く。
    /// <para>新規作成の途中では消すものが無いので、呼んでも何もしない。</para>
    /// </summary>
    public void RequestDelete()
    {
        if (IsNew) return;

        Deleted = true;
    }

    /// <summary>選べるタスクリスト。</summary>
    public IReadOnlyList<SourceChoice> TaskLists { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value ?? string.Empty)) Raise(nameof(CanSave), nameof(ValidationMessage));
        }
    }

    /// <summary>期限を付けるか。外すと「いつやるか未定」になる。</summary>
    public bool HasDue
    {
        get => _hasDue;
        set => Set(ref _hasDue, value);
    }

    public DateOnly Due
    {
        get => _due;
        set => Set(ref _due, value);
    }

    /// <summary>期限を入れる。早入れのボタンから呼ぶ。期限なしだったら付ける。</summary>
    public void SetDue(DateOnly date)
    {
        Due = date;
        HasDue = true;
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    /// <summary>詳細。Google Tasks の <c>notes</c>。</summary>
    public string? Note
    {
        get => _note;
        set
        {
            // 長さの上限に引っかかるかで保存できるかが変わるので、出し直す
            if (Set(ref _note, value)) Raise(nameof(CanSave), nameof(ValidationMessage));
        }
    }

    /// <summary>
    /// URL と添付の欄の近くに出す、小さな一行。Google と同期するタスクでも、この2つはこの PC の
    /// Kado にしか残らないことを、使う人に伝える。
    /// </summary>
    public string LocalOnlyNote => "URL と添付は Kado だけに保存され、Google には送られません";

    /// <summary>
    /// 関連する URL。<b>Kado だけが持ち、Google には送らない</b>。
    /// <para>http または https のものだけ、右クリックメニューの「リンクを開く」から開ける。</para>
    /// </summary>
    public string? Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value)) Raise(nameof(UrlHint));
        }
    }

    /// <summary>
    /// URL の欄に何か入っているのに、リンクとして開けない形のときの説明。問題が無ければ null。
    /// <para>保存は止めない（メモ代わりに残したい人もいる）。開けない理由が分からないのが一番困る。</para>
    /// </summary>
    public string? UrlHint => string.IsNullOrWhiteSpace(_url) || LinkRules.WebUrl(_url) is not null
        ? null
        : "http:// または https:// で始まる URL だけ、リンクとして開けます";

    /// <summary>添えたファイル・フォルダの場所。<b>Kado だけが持ち、Google には送らない</b>。</summary>
    public IReadOnlyList<TaskAttachment> Attachments => _attachments;

    /// <summary>
    /// 場所を開いた結果や、開けなかった理由。無ければ null。
    /// <para>実行形式を実行せずにフォルダを開いたときも、その旨をここに出す。</para>
    /// </summary>
    public string? AttachmentMessage
    {
        get => _attachmentMessage;
        private set => Set(ref _attachmentMessage, value);
    }

    /// <summary>ファイルを選んで場所を足す。複数選べる。同じ場所は足さない。</summary>
    public void AddFiles() => AddPlaces(_dialogs.PickOpenFiles("添付するファイルを選ぶ", AllFilesFilter));

    /// <summary>フォルダを選んで場所を足す。複数選べる。同じ場所は足さない。</summary>
    public void AddFolders() => AddPlaces(_dialogs.PickFolders("添付するフォルダを選ぶ"));

    /// <summary>場所を足す。足したものがあれば、一覧を出し直す。</summary>
    public void AddPlaces(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var added = TaskAttachments.Add(_attachments, paths);

        // 取り消し・すべて重複のときは、一覧にも「触った」印にも手を付けない
        if (added.Count == _attachments.Count) return;

        _attachments = added;
        _attachmentsDirty = true;
        AttachmentMessage = null;
        Raise(nameof(Attachments));
    }

    /// <summary>場所を外す。ファイルそのものには触らない（場所の記録を外すだけ）。</summary>
    public void RemoveAttachment(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var remaining = TaskAttachments.Remove(_attachments, attachment.Path);
        if (remaining.Count == _attachments.Count) return;

        _attachments = remaining;
        _attachmentsDirty = true;
        AttachmentMessage = null;
        Raise(nameof(Attachments));
    }

    /// <summary>
    /// 場所を開く。見つからなければ開かず、理由を <see cref="AttachmentMessage"/> に出す。
    /// <para>判断は <see cref="LinkLauncher.OpenPathAsync"/>（実行形式は実行しない）。右クリックメニューと同じ。</para>
    /// </summary>
    public async Task OpenAttachmentAsync(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        AttachmentMessage = null;

        var result = await _links.OpenPathAsync(attachment.Path).ConfigureAwait(true);

        AttachmentMessage = result.Message;
    }

    /// <summary>
    /// 場所を開く処理で漏れた例外を、画面の文言にして出す。画面の <c>async void</c> が最後の砦として呼ぶ。
    /// </summary>
    public void ReportAttachmentFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        AttachmentMessage = $"開けませんでした（{exception.Message}）";
    }

    public string? TaskListId
    {
        get => _taskListId;
        set
        {
            if (!CanChangeTaskList) return;

            Set(ref _taskListId, value);
        }
    }

    /// <summary>
    /// タスクリスト欄を変えられるか。
    /// <para>
    /// サブタスクと、サブタスクを持つタスクは、Google でリストをまたいで移せない。変えさせても
    /// 同期のたびに断られるだけなので、ここで止めて理由を出す。
    /// </para>
    /// </summary>
    public bool CanChangeTaskList => TaskListLockReason is null;

    /// <summary>タスクリスト欄を変えられない理由。変えられるなら null。</summary>
    public string? TaskListLockReason =>
        _original is null ? null : TaskMapper.MoveBlockReason(_original, _hasChildren);

    public bool CanSave => ValidationMessage is null;

    public string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_title)) return "タイトルを入れてください。";

            // 長すぎるタイトルはそのタスクリスト全体の同期を止める原因になる。入口で止める
            if (_title.Length > TitleMaxLength) return $"タイトルは{TitleMaxLength}文字以内にしてください。";

            if (_note is { Length: > 0 } && _note.Length > NoteMaxLength)
                return $"詳細は{NoteMaxLength}文字以内にしてください。";

            return null;
        }
    }

    /// <summary>入力からタスクを組み立てる。</summary>
    public TaskItem ToModel()
    {
        if (ValidationMessage is { } message) throw new InvalidOperationException(message);

        return new TaskItem
        {
            Id = _original?.Id ?? NewId(),
            Title = _title.Trim(),
            Due = _hasDue ? _due : null,
            IsDone = _isDone,
            // 完了にしているときだけ入れる。もともとの完了日時があれば（Google 側の値を
            // 含む）そのまま残し、無ければ今にする。外していれば消す（項目3）
            CompletedAt = _isDone ? _original?.CompletedAt ?? DateTimeOffset.Now : null,
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note.Trim(),
            TaskListId = _taskListId,

            // Kado だけの項目。Google には送らない（TaskMapper.ToGoogle に入れていない）。
            // 添付は触っていなければ元の文字列のまま返す。読めなかった（壊れた）値を、
            // 開いて保存し直しただけで空に書き換えてしまわないため
            Url = string.IsNullOrWhiteSpace(_url) ? null : _url.Trim(),
            Attachments = _attachmentsDirty ? TaskAttachments.ToJson(_attachments) : _original?.Attachments,

            // Google 側の情報は編集画面で触らない。消さずに引き継ぐ。
            //
            // 落とすと、保存のたびに「Google から一度も受け取っていない」状態に戻り、親子関係と
            // 並び順（Google が持つ値）も空になる。保存の時点では CalendarWorkspace が最新の行から
            // あらためて採り直すので、ここの値は開いた時点のもの
            GoogleTaskId = _original?.GoogleTaskId,
            GoogleTaskListId = _original?.GoogleTaskListId,
            GoogleUpdated = _original?.GoogleUpdated,
            GoogleRaw = _original?.GoogleRaw,
            GoogleMissing = _original?.GoogleMissing ?? false,
            ParentId = _original?.ParentId,
            Position = _original?.Position,
            Source = _original?.Source,
            UpdatedAt = DateTimeOffset.Now,

            // 作成日時と並び順は編集画面で触らない。新規作成（_original が無い）ぶんは
            // CreatedAt が既定値のまま返る。CalendarWorkspace.AddTask がそこを見て
            // 「新規だから今の時刻・末尾の並び順を振る」と判断する
            CreatedAt = _original?.CreatedAt ?? default,
            SortOrder = _original?.SortOrder ?? default,
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..15];
}
