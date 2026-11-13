namespace Kado.Presentation.ViewModels;

/// <summary>
/// 設定画面の分類（左の一覧）。
/// <para>
/// 画面を開くときに、どの分類から開くかを指定できる（同期の状態を押すと「Google」を開く、など）。
/// 並びは画面の並びと同じ。
/// </para>
/// </summary>
public enum SettingsCategory
{
    /// <summary>起動と常駐。</summary>
    Startup,

    /// <summary>画面端に寄せたとき。</summary>
    Edge,

    /// <summary>見え方。</summary>
    Display,

    /// <summary>実働日。</summary>
    Workday,

    /// <summary>通知。</summary>
    Notify,

    /// <summary>Google。</summary>
    Google,

    /// <summary>データ。</summary>
    Data,

    /// <summary>Kado について。</summary>
    About,
}
