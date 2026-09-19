namespace SlideinaCalendar.Presentation;

/// <summary>
/// 祝日の名前を引く。
/// <para>
/// 実際の取得（内閣府の公開 CSV）は Phase 6。それまでは何も返さない実装を挿しておき、
/// 表示側の口だけ通しておく。あとから差し替えるときに月ビューへ手を入れずに済む。
/// </para>
/// <para>
/// 祝日は非稼働日の判定とは別物である点に注意。実働日データは会社の稼働日であり、
/// 祝日でも稼働する場合がある。名前は休みの理由を読ませるための表示にすぎない。
/// </para>
/// </summary>
public interface IHolidaySource
{
    /// <summary>その日の祝日名。祝日でなければ null。</summary>
    string? NameOf(DateOnly date);
}

/// <summary>祝日を一件も返さない実装。祝日データを取り込むまでの既定。</summary>
public sealed class EmptyHolidaySource : IHolidaySource
{
    public static EmptyHolidaySource Instance { get; } = new();

    private EmptyHolidaySource() { }

    public string? NameOf(DateOnly date) => null;
}

/// <summary>あらかじめ渡した一覧から引く実装。取り込み済みの祝日を配るのに使う。</summary>
public sealed class HolidayTable(IReadOnlyDictionary<DateOnly, string> holidays) : IHolidaySource
{
    private readonly IReadOnlyDictionary<DateOnly, string> _holidays =
        holidays ?? throw new ArgumentNullException(nameof(holidays));

    public string? NameOf(DateOnly date) => _holidays.GetValueOrDefault(date);
}
