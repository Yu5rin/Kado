namespace SlideinaCalendar.Presentation.ViewModels;

/// <summary>ショートカット1件。</summary>
/// <param name="Keys">押すもの。「Ctrl＋Alt＋C」。</param>
/// <param name="What">何が起きるか。</param>
public sealed record Shortcut(string Keys, string What);

/// <summary>まとまり（見出し付き）。</summary>
/// <param name="Title">見出し。</param>
/// <param name="Items">中身。</param>
public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Items);

/// <summary>
/// ショートカットの一覧。
/// <para>
/// グローバルホットキーは、押せることを知らないと一生使われない。どこにも書いて
/// いなかったので、⚙ から開ける一覧にした。
/// </para>
/// <para>
/// <b>ここは実装と手で揃える。</b>取り違えると、書いてあるのに効かないことになる。
/// 増やしたら必ずここも足す。
/// </para>
/// </summary>
public static class Shortcuts
{
    /// <summary>並べるもの。</summary>
    public static IReadOnlyList<ShortcutGroup> All { get; } =
    [
        new("どこにいても効く（他のアプリを使っている最中でも）",
        [
            new("Ctrl＋Alt＋C", "SlideinaCalendar を前に出す"),
            new("Ctrl＋Alt＋N", "前に出して、クイック入力に入る"),
        ]),

        new("画面の出しかた",
        [
            new("ツールバーの📌", "ピン留め。画面端に寄せて画面を分割する。"
                + "もう一度押すと元の出しかたに戻る"),
            new("閉じるボタン", "トレイに入る（設定で「そのまま終わる」に変えられる）"),
            new("トレイのアイコン", "左クリックで前に出す。右クリックでメニュー"),
            new("画面端に留める", "「画面端に寄せる」にしてあるとき、端に 0.3 秒ほど"
                + "マウスを留めるとスライドインする"),
        ]),

        new("本体",
        [
            new("Ctrl＋Z", "元に戻す"),
            new("Ctrl＋Y", "やり直す"),
            new("F5", "いますぐ同期"),
        ]),

        new("カレンダーの上で",
        [
            new("Ctrl＋ホイール", "ビューを切り替える。一覧 → 年 → 月 → 週 → 日 と"
                + "見ている範囲が狭まる。選んでいる日はそのまま持っていく"),
            new("ホイール", "前後の月・週・日・年度へ移る"),
            new("ダブルクリック", "その日に予定を追加。予定やタスクの上なら編集"),
            new("右クリック", "予定・タスク・カレンダー一覧のメニュー"),
            new("ドラッグ", "予定やタスクを別の日へ移す"),
            new("Ctrl＋ドラッグ", "移さずに複製する"),
        ]),

        new("入力のとき",
        [
            new("Enter", "クイック入力を確定する"),
            new("Esc", "検索をやめる。編集画面を閉じる"),
            new("↑ ↓", "時刻の欄で 15 分ずつ動かす。開始を動かすと終了も付いてくる"),
            new("ホイール", "時刻の欄で 15 分ずつ動かす"),
        ]),
    ];
}
