using Kado.Presentation.Infrastructure;

namespace Kado.Presentation.Editing;

/// <summary>色の選択肢1つ。</summary>
/// <param name="Value"><c>#rrggbb</c>。</param>
/// <param name="Name">「藍」などの呼び名。</param>
public sealed record ColorChoice(string Value, string Name)
{
    /// <summary>表示名をそのまま返す。コントロールのテンプレートに依らないようにする。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// カレンダー（またはタスクリスト）の編集内容。
/// <para>
/// Google に繋がなくても、このアプリだけで分類を作れるようにするためのもの。
/// 仕事と私用を分けて入れたい、というのは連携の有無に関わらず要る。
/// </para>
/// </summary>
public sealed class CalendarEditorViewModel : ObservableObject
{
    private string _name;
    private string _color;

    /// <summary>新しく作る。</summary>
    /// <param name="isTaskList">タスクリストを作るなら true。色は持たない。</param>
    /// <param name="usedColors">すでに使われている色。同じ色を避けるために渡す。</param>
    public CalendarEditorViewModel(bool isTaskList = false, IEnumerable<string?>? usedColors = null)
    {
        IsNew = true;
        IsTaskList = isTaskList;
        _name = string.Empty;
        _color = CalendarPalette.NextColor(usedColors ?? []);
    }

    /// <summary>すでにあるものを直す。</summary>
    /// <param name="isNameLocked">
    /// 名前を変えさせないか。「Kado」は名前で見分けて日付の行に出しているので、
    /// 変えられると特別な表示が黙って止まる。
    /// </param>
    public CalendarEditorViewModel(
        string id, string name, string? color, bool isTaskList = false, bool isNameLocked = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id;
        IsNew = false;
        IsTaskList = isTaskList;
        IsNameLocked = isNameLocked;
        _name = name;
        _color = color ?? CalendarPalette.ColorFor(name);
    }

    /// <summary>
    /// 名前を変えられないか。
    /// <para>
    /// 「Kado」だけ。名前で見分けて日付の行に出しているので、変えられると
    /// 特別な表示が黙って止まる。色は変えられる。
    /// </para>
    /// </summary>
    public bool IsNameLocked { get; }

    /// <summary>名前欄に添える断り。変えられるときは null。</summary>
    public string? NameLockReason => IsNameLocked
        ? "実働日データの入れ先なので、名前は変えられません"
        : null;

    /// <summary>直す対象。新規なら null。</summary>
    public string? Id { get; }

    public bool IsNew { get; }

    /// <summary>タスクリストか。見出しと、色欄を出すかどうかが変わる。</summary>
    public bool IsTaskList { get; }

    /// <summary>画面の見出し。</summary>
    public string HeaderText => (IsTaskList, IsNew) switch
    {
        (true, true) => "タスクリストの追加",
        (true, false) => "タスクリストの編集",
        (false, true) => "カレンダーの追加",
        (false, false) => "カレンダーの編集",
    };

    /// <summary>色を選ばせるか。タスクリストは色を持たない。</summary>
    public bool HasColor => !IsTaskList;

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? string.Empty)) Raise(nameof(CanSave), nameof(ValidationMessage));
        }
    }

    /// <summary>選んだ色（<c>#rrggbb</c>）。</summary>
    public string Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    /// <summary>選べる色。</summary>
    public IReadOnlyList<ColorChoice> Colors { get; } =
        CalendarPalette.Colors
            .Select((value, i) => new ColorChoice(value, CalendarPalette.Names[i]))
            .ToArray();

    public bool CanSave => ValidationMessage is null;

    public string? ValidationMessage =>
        string.IsNullOrWhiteSpace(_name)
            ? IsTaskList ? "タスクリスト名を入れてください。" : "カレンダー名を入れてください。"
            : null;

    /// <summary>整えた名前。</summary>
    public string TrimmedName => _name.Trim();
}
