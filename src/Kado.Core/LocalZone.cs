namespace Kado.Core;

/// <summary>
/// 「いまの端末のタイムゾーン」の取得口。
/// <para>
/// 既定は <see cref="TimeZoneInfo.Local"/> を<b>毎回</b>読む。作った時点の値を握らない
/// （何日も動き続けるあいだに端末のタイムゾーンが変わっても、
/// <see cref="TimeZoneInfo.ClearCachedData"/> のあとから新しいゾーンになる）。
/// </para>
/// <para>
/// テストが端末のタイムゾーンの変更を再現するための差し替え口でもある。環境変数 <c>TZ</c> は
/// Linux の .NET しか読まず、Windows の .NET は OS（レジストリ）から読むので、<c>TZ</c> の
/// 書き換えでは Windows で再現できない。製品のコードは <see cref="Current"/> だけを見る。
/// </para>
/// </summary>
public static class LocalZone
{
    private static volatile TimeZoneInfo? _override;

    /// <summary>いまの端末のタイムゾーン。差し替え中はその値、そうでなければ毎回 <see cref="TimeZoneInfo.Local"/>。</summary>
    public static TimeZoneInfo Current => _override ?? TimeZoneInfo.Local;

    /// <summary>
    /// テスト専用。端末のタイムゾーンを <paramref name="zone"/> に差し替える。
    /// 返した値を <see cref="IDisposable.Dispose"/> すると、差し替える前の状態に戻る。
    /// <para>プロセス全体に効くので、並列にしないコレクションの中で使い、必ず戻すこと。</para>
    /// </summary>
    public static IDisposable OverrideForTest(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var previous = _override;
        _override = zone;
        return new Restore(previous);
    }

    /// <summary>テスト専用。差し替えを、<see cref="OverrideForTest"/> を呼ぶ前の値に戻す。</summary>
    private sealed class Restore(TimeZoneInfo? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _override = previous;
        }
    }
}
