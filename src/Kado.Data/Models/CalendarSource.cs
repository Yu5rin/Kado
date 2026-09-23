using System.Text.Json;

namespace Kado.Data.Models;

/// <summary>
/// カレンダー1つ。
/// <para>
/// これまでは予定の <c>calendar_id</c> から名前を拾い、色は名前から作っていた。
/// Google から取り込めば本物の名前と色になる（要件書 6.3）。
/// </para>
/// </summary>
public sealed record CalendarSource
{
    /// <summary>Google 側のカレンダー ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Google 側の名前。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>利用者が付け替えた表示名。Google Calendar の <c>summaryOverride</c>。</summary>
    public string? SummaryOverride { get; init; }

    /// <summary>画面に出す名前。付け替えがあればそちらを使う。</summary>
    public string DisplayName => SummaryOverride is { Length: > 0 } name ? name : Summary;

    /// <summary>色（<c>#rrggbb</c>）。予定の帯と左パネルの色見本に使う。</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>文字色（<c>#rrggbb</c>）。</summary>
    public string? ForegroundColor { get; init; }

    /// <summary>既定のカレンダーか。</summary>
    public bool IsPrimary { get; init; }

    /// <summary>左パネルでチェックが入っているか。</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>並び順。</summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// このカレンダーの予定を、既定で通知するか。
    /// <para>左パネルのベルで切り替える。予定ごとの指定があれば、そちらが勝つ。</para>
    /// </summary>
    public bool NotifyDefault { get; init; } = true;

    /// <summary>最後に Google から受け取った姿。</summary>
    public string? GoogleRaw { get; init; }

    /// <summary>ローカルでの更新時刻。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// こちらから書けないカレンダーか。
    /// <para>
    /// 祝日・誕生日・他人から共有されたものは読むだけ。送ろうとしても断られる。
    /// 判断は取り込んだときの <c>accessRole</c> で行う（<c>owner</c> / <c>writer</c> だけが
    /// 書ける）。<b>同期処理（送るかどうか）と画面（編集・削除・ドラッグを止めるかどうか）の
    /// 両方がここを見る。</b>判定を2箇所に分けて持つと、どちらかだけ直し忘れて食い違う。
    /// </para>
    /// </summary>
    public bool IsReadOnly
    {
        get
        {
            if (GoogleRaw is not { Length: > 0 } raw) return false;

            try
            {
                using var document = JsonDocument.Parse(raw);

                if (!document.RootElement.TryGetProperty("accessRole", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var role = value.GetString();

                return role is not null &&
                       !string.Equals(role, "owner", StringComparison.Ordinal) &&
                       !string.Equals(role, "writer", StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                // 読めないなら書けると見なす。書けないものへ送れば断られるだけで、
                // 書けるものを読み取り専用にしてしまうより害が小さい
                return false;
            }
        }
    }
}

/// <summary>タスクリスト1つ。カレンダーとは独立した同期経路を持つ（要件書 6.3）。</summary>
public sealed record TaskListSource
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>画面に出す名前。カレンダー側と揃えておく。</summary>
    public string DisplayName => Title;

    public bool IsVisible { get; init; } = true;

    public int SortOrder { get; init; }

    public string? GoogleRaw { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
