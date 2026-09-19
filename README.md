# SlideinaCalendar

Windows デスクトップ向けの予定・タスク管理アプリ。画面端に常駐させて一日中使うツールで、
Google カレンダー／Google タスクと同期しつつ、**会社の実働日（稼働日）を軸に日数を数えられる**
ことが他製品との差になる。

仕様の正は `docs/SlideinaCalendar-requirements-v1.0.md`。

| 項目 | 選定 |
|---|---|
| ランタイム | .NET 8（LTS） |
| UI | WPF（フルネイティブ。WebView2 や Electron は使わない） |
| データ保存 | SQLite（Phase 2） |
| Excel 読込 | ClosedXML |
| シェル統合 | Win32 P/Invoke（`SHAppBarMessage` ほか） |

## 構成

```
SlideinaCalendar.sln
├ src/
│  ├ SlideinaCalendar.Core/      … 繰り返し・実働日・日付計算。UI 非依存
│  ├ SlideinaCalendar.Data/      … SQLite（Phase 2）
│  ├ SlideinaCalendar.Google/    … Calendar / Tasks 同期（Phase 4）
│  ├ SlideinaCalendar.Shell/     … AppBar・トレイ・通知（Phase 5）
│  └ SlideinaCalendar.App/       … WPF 本体（Phase 3）
├ tests/
│  └ SlideinaCalendar.Core.Tests/    … xUnit
└ samples/
   └ AppBarProbe/                … AppBar 成立性の検証用プロトタイプ
```

## ビルドとテスト

```
dotnet build
dotnet test
```

WPF プロジェクト（`SlideinaCalendar.App` / `AppBarProbe`）は `net8.0-windows` だが、
`Directory.Build.props` で `EnableWindowsTargeting` を立てているため Linux の CI でもビルドできる。
**実行は Windows が必要。**

AppBar の検証手順は `samples/AppBarProbe/README.md` を参照。

## Phase 1 の実装範囲

`SlideinaCalendar.Core` の 5 クラスと、その単体テスト。UI はまだ作らない。
日付は `DateOnly` / `TimeOnly` を使い、`DateTime` は使わない。

### WorkingDayCalendar

実働日の集合を保持し、判定と集計を行う不変オブジェクト。
稼働判定は `HashSet` による O(1)、範囲集計は昇順配列への二分探索で O(log n)。

```csharp
bool IsWorkingDay(DateOnly date);
bool HasDataFor(DateOnly date);          // 実働日データの登録範囲内か
int? IndexInMonth(DateOnly date);        // その月の何実働日目か。非稼働日は null
int CountInMonth(int year, int month);
IReadOnlyList<Milestone> MilestonesOn(DateOnly date);
```

稼働日は「登録された日付の集合」であって「土日祝を除いた日」ではない。
登録範囲外は土日祝から推測せず `HasDataFor` が false を返し、呼び出し側が暦日にフォールバックする。

### WorkingDayMath

```csharp
int? CountBetween(DateOnly from, DateOnly to);        // 期間の実働日数
DateOnly? AddWorkingDays(DateOnly baseDate, int n);   // 基準日＋N実働日
DateOnly? PreviousWorkingDay(DateOnly date);
DateOnly? NextWorkingDay(DateOnly date);
```

境界規則は **`from` は数えず `to` は数える**。半開区間 `(from, to]` を数えると考えればよい。
この規則により `CountBetween(base, AddWorkingDays(base, n)) == n` が常に成り立つ（テストで担保）。

### DueDateFormatter

```csharp
DueText Format(DateOnly due, DateOnly today);
```

| 条件 | 出力 |
|---|---|
| 実働日データがある範囲 | `残り 3実働日` |
| データが無い範囲 | `残り 189日` |
| 期限が今日 | `今日まで` |
| 期限を過ぎている | `3実働日 遅れ` |
| 期限日そのものが非稼働日 | `残り 2実働日（9/25まで）` |

**今日は数えず、期限日は数える。** データ範囲の内外が混在する場合は暦日側に倒す。

### RecurrenceRule

指定文字列は **RFC 5545 の RRULE のサブセット**を採る。独自形式にしないのは、
Phase 4 の Google Calendar 同期でそのまま受け渡せるようにするため。

```
FREQ=DAILY;INTERVAL=2
FREQ=WEEKLY;BYDAY=MO,WE,FR
FREQ=MONTHLY;BYMONTHDAY=-1          … 月末
FREQ=YEARLY;BYMONTH=9;BYMONTHDAY=19
FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231
```

対応種別は `RecurrenceRule.RegisterPattern` で後から足せる（要件書 11 章のとおり未確定のため）。

### WorkdayFileImporter

会社配布 Excel の取り込み。**マイルストーン名称を固定 4 種で決め打ちしない。**
D 列に現れた文字列をそのまま種類として登録するので、将来の名称追加で壊れない。

**既存データは全置換しない。ファイルに含まれる期間だけを置き換える。**
稼働日とマイルストーンは期間が異なるため（実ファイルでは稼働日 2023/1/5〜2026/3/31、
マイルストーン 2025/1/10〜2026/3/26）、それぞれの期間で判定する。
この処理は `WorkingDayCalendar.Merge` にある。

不正な行は例外を投げず `ImportResult.Warnings` に積んで処理を続ける。

## 設計上の決定

仕様に明記が無く、実装で決めた点を挙げる。異論があれば作り直せる範囲に収めてある。

- **`AddWorkingDays` の負値**は「基準日より前の `|n|` 番目の実働日」。`PreviousWorkingDay(d)` と
  `AddWorkingDays(d, -1)` が一致する。基準日が非稼働日でも動く
- **`CountBetween` は `from > to` のとき負の値**を返す。向きを対称に扱えるようにした
- **超過していて期限が非稼働日の場合も寄せ先を明示する**（`2実働日 遅れ（9/18まで）`）。
  数字の根拠になる日が読めないと信用できないため
- **実働日換算で 0 日の超過だけ暦日に倒す**。今日が非稼働日で、寄せた期限との間に実働日が
  1 日も無いときに `0実働日 遅れ` となってしまうため、ここだけ `1日 遅れ（9/25まで）` とする
- **稼働日が 1 件も読めないファイルは例外**にする。行単位の不正は警告に留めるが、
  稼働日が皆無のファイルは実働日データとして成立しないため
- **ヘッダ行は警告にしない**。実ファイルの B5 には見出し「配布元稼働日」が入っている。
  データが始まる前の非日付セルは黙って読み飛ばし、データ開始後のものだけ警告する

## 開発フェーズ

| Phase | 内容 | 状態 |
|---|---|---|
| 1 | Core（繰り返し判定・実働日計算・期限カウント）＋単体テスト | **今回** |
| 2 | SQLite データ層＋旧 JSON インポート | |
| 3 | WPF ウィンドウモード＋予定・タスク CRUD＋Undo | |
| 4 | Google 同期（Calendar → Tasks の順） | |
| 5 | Shell 統合（トレイ→サイドバー→ホバー→ピン／AppBar） | プロトタイプのみ先行 |
| 6 | 通知・フィード・祝日取得・バックアップ・年ストリップ・一覧ビュー | |

Phase 5 は最重要要件（要件書 2 章）そのものであり最大の技術リスクなので、
`samples/AppBarProbe` で成立性だけを先に確認する。
