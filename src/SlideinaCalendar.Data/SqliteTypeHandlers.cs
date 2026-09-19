using System.Data;
using System.Globalization;
using Dapper;

namespace SlideinaCalendar.Data;

/// <summary>
/// <see cref="DateOnly"/> / <see cref="TimeOnly"/> を SQLite に保存するための変換。
/// <para>
/// SQLite に日付型は無いので文字列で持つ。<c>yyyy-MM-dd</c> と <c>HH:mm</c> の固定書式にすると
/// 辞書順と時系列が一致するため、<c>ORDER BY</c> や範囲検索がそのまま使える。
/// </para>
/// <para>
/// 書式はカルチャに依存させない。ロケールによって年月日の並びが変わると、
/// 既存のデータベースが読めなくなる。
/// </para>
/// </summary>
public static class SqliteTypeHandlers
{
    /// <summary>日付の保存書式。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>時刻の保存書式。</summary>
    public const string TimeFormat = "HH:mm";

    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>Dapper に変換規則を登録する。何度呼んでも一度しか登録しない。</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;

            // Dapper は組み込みの型マップを型ハンドラより先に見る。外しておかないと
            // 独自の変換が呼ばれず、DateTimeOffset が既定の文字列表現で保存されてしまう。
            foreach (var type in (Type[])[
                typeof(DateOnly), typeof(DateOnly?),
                typeof(TimeOnly), typeof(TimeOnly?),
                typeof(DateTimeOffset), typeof(DateTimeOffset?)])
            {
                SqlMapper.RemoveTypeMap(type);
            }

            SqlMapper.AddTypeHandler(new DateOnlyHandler());
            SqlMapper.AddTypeHandler(new TimeOnlyHandler());
            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            _registered = true;
        }
    }

    /// <summary>日付を保存用の文字列にする。</summary>
    public static string ToText(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>時刻を保存用の文字列にする。</summary>
    public static string ToText(TimeOnly value) => value.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>保存用の文字列を日付に戻す。読めなければ null。</summary>
    public static DateOnly? TryParseDate(string? text) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? v : null;

    /// <summary>保存用の文字列を時刻に戻す。読めなければ null。</summary>
    public static TimeOnly? TryParseTime(string? text) =>
        TimeOnly.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? v : null;

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            string s => DateOnly.ParseExact(s, DateFormat, CultureInfo.InvariantCulture),
            DateTime d => DateOnly.FromDateTime(d),
            _ => throw new DataException($"日付として読めません: {value?.GetType().Name ?? "null"}"),
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = ToText(value);
        }
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override TimeOnly Parse(object value) => value switch
        {
            string s => TimeOnly.ParseExact(s, TimeFormat, CultureInfo.InvariantCulture),
            DateTime d => TimeOnly.FromDateTime(d),
            _ => throw new DataException($"時刻として読めません: {value?.GetType().Name ?? "null"}"),
        };

        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = ToText(value);
        }
    }

    /// <summary>
    /// 更新時刻は epoch ミリ秒で持つ。タイムゾーンの解釈が入らず、比較も並べ替えも整数で済む。
    /// </summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
            int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                => DateTimeOffset.FromUnixTimeMilliseconds(ms),
            _ => throw new DataException($"更新時刻として読めません: {value?.GetType().Name ?? "null"}"),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = value.ToUnixTimeMilliseconds();
        }
    }
}
