using Kado.Data.Models;
using Kado.Google.Mapping;

namespace Kado.Presentation.Editing;

/// <summary>編集画面を開いている間に起きたこと。</summary>
public enum EditConflictKind
{
    /// <summary>何も無い。そのまま保存してよい。</summary>
    None,

    /// <summary>Google 側の内容が変わっていた。上書きしてよいか、保存の前に尋ねる。</summary>
    GoogleChanged,

    /// <summary>行が無くなっていた（Google 側で消され、同期が手元からも消した）。保存しない。</summary>
    Deleted,
}

/// <summary>
/// 編集画面を開いている間に、Google 側で予定・タスクが変わったか、消えたかを判断する。
/// <para>
/// 裏の同期は編集画面（モーダル）の間も走る。そのまま保存すると、同僚が Google で変えた内容を
/// 警告なしで上書きしてしまう。保存の前に、<b>開いた時点の姿</b>と<b>いま保存されている姿</b>を比べる。
/// </para>
/// <para>
/// 見るのは Google 側の姿だけ（更新時刻と、最後に受け取った生データ）。手元の中身や入れ先の希望が
/// 変わっていても、Google 側が動いていなければ競合ではない。入れ先の実際の場所
/// （<c>GoogleCalendarId</c>）だけが変わったとき（こちらの move が済んだ）も同じ。
/// </para>
/// </summary>
public static class EditConflict
{
    /// <summary>予定版。<paramref name="latest"/> は保存の時点の行（無ければ null）。</summary>
    public static EditConflictKind Check(CalendarEvent opened, CalendarEvent? latest)
    {
        ArgumentNullException.ThrowIfNull(opened);

        if (latest is null) return EditConflictKind.Deleted;

        return GoogleChanged(opened.GoogleUpdated, opened.GoogleRaw, latest.GoogleUpdated, latest.GoogleRaw)
            ? EditConflictKind.GoogleChanged
            : EditConflictKind.None;
    }

    /// <summary>タスク版。</summary>
    public static EditConflictKind Check(TaskItem opened, TaskItem? latest)
    {
        ArgumentNullException.ThrowIfNull(opened);

        if (latest is null) return EditConflictKind.Deleted;

        return GoogleChanged(opened.GoogleUpdated, opened.GoogleRaw, latest.GoogleUpdated, latest.GoogleRaw)
            ? EditConflictKind.GoogleChanged
            : EditConflictKind.None;
    }

    private static bool GoogleChanged(string? openedUpdated, string? openedRaw, string? latestUpdated, string? latestRaw) =>
        !string.Equals(openedUpdated, latestUpdated, StringComparison.Ordinal) ||
        !GoogleJson.SameContent(openedRaw, latestRaw);
}
