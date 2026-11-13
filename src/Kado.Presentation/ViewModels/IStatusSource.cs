namespace Kado.Presentation.ViewModels;

/// <summary>
/// 画面の下に出す知らせの出どころ。
/// <para>
/// 設定の中で押した操作（バックアップの保存・書き出し・重複整理・クライアント設定の取り込みなど）の結果は、
/// 本体の窓の帯に出る。設定の窓が前にあると見えないので、設定の窓の下にも同じものを出す。
/// 設定の窓は、この口だけを知っていればよい（本体の ViewModel を丸ごとは持たない）。
/// </para>
/// </summary>
public interface IStatusSource
{
    /// <summary>いま出している知らせ。無ければ null。</summary>
    string? StatusMessage { get; }

    /// <summary>出している知らせが失敗か。</summary>
    bool IsStatusFailure { get; }

    /// <summary>知らせの中身か種類が変わった。</summary>
    event EventHandler? StatusChanged;
}
