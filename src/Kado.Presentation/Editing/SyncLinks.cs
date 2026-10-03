using Kado.Data.Models;
using Kado.Google.Mapping;

namespace Kado.Presentation.Editing;

/// <summary>
/// 予定・タスクの「中身」と「Google との結び付き」を分ける。
/// <para>
/// 使う人が編集する項目（題・日時・繰り返し・場所・メモ・通知・色・入れ先の希望・完了状態など）が
/// <b>中身</b>。Google の ID・実際にいる入れ先・控えた生データ・更新時刻・状態・親子関係と
/// 並び順（タスク）・「Google に無い」印が<b>結び付き</b>で、同期だけが書き換える。
/// </para>
/// <para>
/// 書き込むときは、中身は編集の前後（または編集画面）から採り、結び付きは<b>書き込む時点の
/// 最新の行</b>から採る。編集を開いたとき・実行したときの結び付きを持ち回ると、その間に
/// 同期が進めた入れ先や控えを古い値で巻き戻し、Google 側に二重にできる。
/// </para>
/// </summary>
public static class SyncLinks
{
    /// <summary>
    /// <paramref name="content"/> の中身に、<paramref name="latest"/> の結び付きを合わせた姿。
    /// </summary>
    /// <param name="content">中身を採る姿。</param>
    /// <param name="other">
    /// この編集のもう一方の姿（元に戻すなら編集後、やり直すなら編集前）。添付を触った編集かの
    /// 見分けに使う。
    /// </param>
    /// <param name="latest">いま保存されている行。結び付きを採る。</param>
    public static CalendarEvent Merge(CalendarEvent content, CalendarEvent other, CalendarEvent latest)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(latest);

        return content with
        {
            GoogleEventId = latest.GoogleEventId,
            GoogleCalendarId = latest.GoogleCalendarId,
            GoogleRaw = latest.GoogleRaw,
            GoogleUpdated = latest.GoogleUpdated,
            GoogleMissing = latest.GoogleMissing,
            Status = latest.Status,
            Source = latest.Source,

            // 添付の指定は「足した・外した」をしたときだけ持つ（送り終えると空に戻る）。
            // 添付を触った編集を戻すとき、こちらの姿が「触っていない」（空）だと、足した添付が
            // 外れない。その姿の添付を、あらためて指定として持たせる
            PendingAttachments = content.PendingAttachments is not null || other.PendingAttachments is null
                ? content.PendingAttachments
                : EventMapper.ToPendingAttachmentsJson(EventMapper.EffectiveAttachments(content)),
        };
    }

    /// <summary>タスク版。親子関係と並び順は Google が持つので、結び付きとして最新から採る。</summary>
    public static TaskItem Merge(TaskItem content, TaskItem latest)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(latest);

        return content with
        {
            GoogleTaskId = latest.GoogleTaskId,
            GoogleTaskListId = latest.GoogleTaskListId,
            GoogleRaw = latest.GoogleRaw,
            GoogleUpdated = latest.GoogleUpdated,
            GoogleMissing = latest.GoogleMissing,
            Source = latest.Source,
            ParentId = latest.ParentId,
            Position = latest.Position,
        };
    }

    /// <summary>
    /// Google との結び付きを外した姿。Google の ID を持たない新しい予定として送られる。
    /// <para>
    /// 控えた生データを捨てると、そこに入っていた添付が落ちる。付いていた添付は、新しい予定にも
    /// 付くよう指定として持たせる（ドライブのファイルを指すだけなので、新しい予定にも付けられる）。
    /// </para>
    /// </summary>
    public static CalendarEvent AsNew(CalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var attachments = value.PendingAttachments is null ? EventMapper.EffectiveAttachments(value) : [];

        return value with
        {
            GoogleEventId = null,
            GoogleCalendarId = null,
            GoogleRaw = null,
            GoogleUpdated = null,
            GoogleMissing = false,
            PendingAttachments = value.PendingAttachments is null && attachments.Count > 0
                ? EventMapper.ToPendingAttachmentsJson(attachments)
                : value.PendingAttachments,
        };
    }

    /// <summary>タスク版。親（Google の ID を指す）と並び順も、新しいタスクには持ち越せない。</summary>
    public static TaskItem AsNew(TaskItem value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value with
        {
            GoogleTaskId = null,
            GoogleTaskListId = null,
            GoogleRaw = null,
            GoogleUpdated = null,
            GoogleMissing = false,
            ParentId = null,
            Position = null,
        };
    }

    /// <summary>
    /// 削除の記録の持ち主。<b>Google で実際にいる</b>カレンダー、分からなければ入れ先の希望。
    /// <para>
    /// 入れ先の希望（<see cref="CalendarEvent.CalendarId"/>）を変えただけで、まだ move を
    /// 送っていないとき、両者は違う。希望のほうを持ち主にすると、同期は希望先へ削除を投げて
    /// 404（無い）を受け取り、「すでに消えた」として記録を捨てる。本当にいる元のカレンダーには
    /// 届かず、予定が Google に残る。
    /// </para>
    /// </summary>
    public static string? OwnerOf(CalendarEvent value) =>
        value.GoogleCalendarId is { Length: > 0 } actual ? actual : value.CalendarId;

    /// <summary>タスク版。</summary>
    public static string? OwnerOf(TaskItem value) =>
        value.GoogleTaskListId is { Length: > 0 } actual ? actual : value.TaskListId;
}
