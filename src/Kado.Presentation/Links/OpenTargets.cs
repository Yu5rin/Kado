using Kado.Data.Models;
using Kado.Data.Repositories;
using Kado.Google.Mapping;
using Kado.Presentation.ViewModels;

namespace Kado.Presentation.Links;

/// <summary>右クリックメニューの「添付を開く」に並べる1件。</summary>
/// <param name="Label">メニューに出す名前（ファイル名）。</param>
/// <param name="Target">開く対象。予定なら <see cref="EventAttachment"/>、タスクなら <see cref="TaskAttachment"/>。</param>
public sealed record AttachmentItem(string Label, object Target);

/// <summary>メニューに並べる、開く先1件。</summary>
/// <param name="Label">表示名。</param>
/// <param name="Command">押したときのコマンド。</param>
/// <param name="Parameter">コマンドに渡すもの。</param>
public sealed record OpenMenuItem(string Label, System.Windows.Input.ICommand Command, object? Parameter);

/// <summary>
/// 予定・タスク1件から開ける先（リンクと添付）を読み出したもの。右クリックメニューの出し分けに使う。
/// <para>
/// 予定とタスクで持ち方が違う（予定は <c>Url</c> と Google ドライブの添付、タスクは <c>Url</c> と
/// ファイルの場所）が、メニューの形は同じ。違いはここで吸収して、メニュー側は
/// 「リンクがあるか」「添付の一覧」だけを見る。
/// </para>
/// <para>
/// <b>開けるものだけを載せる。</b>リンクは http/https、予定の添付は https だけ（<see cref="LinkRules"/>）。
/// 載っていれば項目を出し、無ければ項目ごと隠す。タスクのファイルの場所は、開く時に存在を確かめるので
/// ここでは全部載せる。
/// </para>
/// </summary>
public sealed class OpenTargets
{
    /// <summary>開く先が1つも無い。</summary>
    public static readonly OpenTargets None = new(null, []);

    private OpenTargets(string? linkUrl, IReadOnlyList<AttachmentItem> attachments)
    {
        LinkUrl = linkUrl;
        Attachments = attachments;
    }

    /// <summary>開いてよいリンク（ブラウザに渡す形）。http/https でなければ、または無ければ null。</summary>
    public string? LinkUrl { get; }

    /// <summary>開ける添付。</summary>
    public IReadOnlyList<AttachmentItem> Attachments { get; }

    /// <summary>「リンクを開く」を出すか。</summary>
    public bool HasLink => LinkUrl is not null;

    /// <summary>「添付を開く」を出すか。</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>どれか1つでも開けるか。区切り線を出すかの判断に使う。</summary>
    public bool HasAny => HasLink || HasAttachments;

    /// <summary>予定から読む。添付は Google から受け取った姿、または送る前の指定（<see cref="EventMapper.EffectiveAttachments"/>）。</summary>
    public static OpenTargets For(CalendarEvent? value)
    {
        if (value is null) return None;

        var link = LinkRules.WebUrl(value.Url);
        var attachments = EventMapper.EffectiveAttachments(value)
            .Where(a => LinkRules.IsHttps(a.FileUrl))
            .Select(a => new AttachmentItem(LabelOf(a), a))
            .ToArray();

        return link is null && attachments.Length == 0 ? None : new OpenTargets(link, attachments);
    }

    /// <summary>タスクから読む。</summary>
    public static OpenTargets For(TaskItem? value)
    {
        if (value is null) return None;

        var link = LinkRules.WebUrl(value.Url);
        var attachments = TaskAttachments.Read(value.Attachments)
            .Select(a => new AttachmentItem(a.Name, a))
            .ToArray();

        return link is null && attachments.Length == 0 ? None : new OpenTargets(link, attachments);
    }

    /// <summary>
    /// 右クリックされた行の中身（画面の <c>DataContext</c>）から読む。
    /// <para>行の型は画面ごとに違う（月のチップ・右ペインの行・時間軸のブロックなど）ので、ここで振り分ける。</para>
    /// </summary>
    public static OpenTargets From(object? entry) => entry switch
    {
        EventChipViewModel chip => For(chip.Scheduled.Source),
        DayEventViewModel row => For(row.Scheduled.Source),
        TimeBlockViewModel block => For(block.Source),
        MilestoneViewModel milestone => For(milestone.Source),
        ScheduledTask task => For(task.Source),
        TaskListItemViewModel item => For(item.Task),
        CalendarEvent value => For(value),
        TaskItem value => For(value),
        _ => None,
    };

    /// <summary>予定の添付の表示名。題が無ければ URL の最後の部分、それも無ければ「添付」。</summary>
    private static string LabelOf(EventAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.Title)) return attachment.Title;

        var last = attachment.FileUrl.TrimEnd('/').Split('/').LastOrDefault();

        return string.IsNullOrWhiteSpace(last) ? "添付" : last;
    }
}
