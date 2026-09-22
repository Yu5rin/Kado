namespace SlideinaCalendar.App.Shell;

/// <summary>
/// いま開いている <c>ContextMenu</c>・<c>Popup</c> の数を数える（項目3）。
/// <para>
/// <c>Window</c> や Win32 の呼び出しを挟まない、開閉のカウントだけの純粋な計算。
/// スライドで出しているとき、自分が出したメニューやポップアップ（「…」の畳んだ
/// メニュー・⚙メニュー・各種の右クリックメニュー・ComboBox のドロップダウン・
/// DatePicker の暦など）が開いているあいだは <c>ShellController.SlideOutIfIdle</c>
/// を素通りさせるための下ごしらえ。実際に配線するのは <see cref="PopupActivityHooks"/>
/// （WPF に依存するので、そちらはテストできない）。
/// </para>
/// </summary>
internal sealed class PopupActivityTracker
{
    private int _openCount;

    /// <summary>いま開いている数。テストのために公開しておく。</summary>
    internal int OpenCount => _openCount;

    /// <summary>1つ以上、開いているか。</summary>
    internal bool IsAnyOpen => _openCount > 0;

    /// <summary>1つ開いた。</summary>
    internal void Opened() => _openCount++;

    /// <summary>
    /// 1つ閉じた。
    /// <para>
    /// <b>0未満へは落とさない。</b> 開いた通知を取りこぼした状態で閉じた通知だけが
    /// 重ねて来ても、マイナスのまま溜め込んで「次に本当に1つ開いたのに0扱いされる」
    /// という逆向きの不具合を防ぐ。
    /// </para>
    /// </summary>
    internal void Closed()
    {
        if (_openCount > 0) _openCount--;
    }
}
