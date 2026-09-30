using System.Xml;
using System.Xml.Linq;

namespace Kado.Presentation.Update;

/// <summary>
/// GitHub の <c>releases.atom</c> から、いちばん新しいリリースのタグを読む。
/// <para>
/// 応答は途中の中継（会社のプロキシなど）に差し替えられうるので、読み方を絞ってある。
/// </para>
/// <list type="bullet">
/// <item>XML は <b>外部実体を読まない</b>（DTD を禁止し、リゾルバも渡さない）。
/// DOCTYPE 付きの応答は読めないものとして捨てる</item>
/// <item><b>大きさに上限</b>を設ける（<see cref="MaxBytes"/>）。実物は数十 KB</item>
/// <item>タグは <c>vX.Y.Z</c>（<see cref="UpdateLinks.TryParseTag"/>）だけを採る。
/// それ以外は無視する</item>
/// </list>
/// <para>
/// <b>プレリリースについて。</b>Atom フィードはプレリリースも載せるが、API の
/// <c>releases/latest</c> は安定版だけを返す。プレリリースが最新の間は「Atom では新しい・API では
/// 最新版」になり、毎回 API まで進む（節約は効かないが、API が読めれば誤って案内はしない）。
/// ただし API が上限で失敗すると、組み立てた URL でプレリリースを案内してしまう。
/// Kado はプレリリースを使っていない運用なので許容している。使い始めるなら、Atom の
/// entry からプレリリースを除く条件が要る（<c>vX.Y.Z-beta</c> のようなタグは表記の検査で
/// はじかれるが、<c>vX.Y.Z</c> のままプレリリースにしたものは拾ってしまう）。
/// </para>
/// </summary>
public static class AtomFeed
{
    /// <summary>受け取ってよい大きさ。実物（数十 KB）よりずっと大きい値にしてある。</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// Atom のテキストから、いちばん新しいタグを取り出す。読めなければ null。
    /// <para>
    /// 並び順には頼らず、読めたタグのうち版として最大のものを選ぶ。新しい順に並ぶのが
    /// 普通でも、並びが変わったときに古い版を「最新」と判断しないため。
    /// </para>
    /// </summary>
    public static string? ReadLatestTag(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxBytes) return null;

        try
        {
            var settings = new XmlReaderSettings
            {
                // 外部実体・DTD は読まない。DOCTYPE があれば XmlException で落ちる
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                MaxCharactersFromEntities = 0,
            };

            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);

            var feed = XDocument.Load(reader);
            XNamespace atom = "http://www.w3.org/2005/Atom";

            string? bestTag = null;
            Version? bestVersion = null;

            foreach (var entry in feed.Root?.Elements(atom + "entry") ?? [])
            {
                var href = entry.Elements(atom + "link")
                    .Select(link => (string?)link.Attribute("href"))
                    .FirstOrDefault(h => !string.IsNullOrEmpty(h));

                if (href is null) continue;

                // タグ名はリリースのページの URL（.../releases/tag/v1.0.5）の末尾に出る
                var tag = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());

                if (!UpdateLinks.TryParseTag(tag, out var version)) continue;
                if (bestVersion is not null && version <= bestVersion) continue;

                bestVersion = version;
                bestTag = tag;
            }

            return bestTag;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or UriFormatException)
        {
            // 壊れた応答が返っても、呼び出し側は API で確かめられる。ここでは黙って諦める
            return null;
        }
    }
}
