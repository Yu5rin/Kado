# テストデータ

## inaCalendar-backup-sample.json

旧 Edge 拡張 inaCalendar（v1.19.3 / エクスポート形式 version 3）のバックアップ JSON。

**実データから構造を保ったまま匿名化したもの。** 予定のタイトル・場所・メモは
ダミーに差し替え、Google の各種 ID はハッシュから決定的に生成した偽 ID に置き換えてある
（同じ元 ID は同じ偽 ID になるので、予定どうしの参照関係は保たれる）。

ありふれた予定は間引いてあるが、**移行で判断が要るケースは実データの全件を残している。**

| ケース | 件数 | なぜ残すか |
|---|---|---|
| ToDo フラグつき（`todo`） | 13 | タスクへの変換対象（要件書 8 章）。うち完了済み 11、未完了 2 |
| Google Tasks 連携つき（`gtaskId`） | 11 | 変換後もリンクを保つ必要がある |
| 複数日予定（`endDate`） | 22 | 開始日のキーと終了日から期間を組み立てる |
| メモつき（`note`） | 15 | 任意項目の引き継ぎ |
| インポート由来（`src`） | 134 | 過去の移行で入った予定 |
| Google 未同期 | 13 | ローカルのみの予定。再同期の対象外 |
| 除外日つき繰り返し（`except`） | 1 | RRULE の EXDATE にあたる |
| 2099 年の予定 | 1 | 遠い未来の外れ値。日付処理が壊れないこと |

稼働日 753 件（2023-01-05〜2026-03-31）と設定 14 項目は実データのまま。
いずれも個人情報を含まず、`ツール用実働日.xlsx` から取り込んだ内容と一致する。

### 元データの構造

```
app            "inaCalendar"
version        3
exportedAt     ISO 8601
userEvents     { "YYYY-MM-DD": [ 予定, ... ] }
recurringEvents[ 繰り返し予定, ... ]
settings       { fontSize, show, notify, hours, summary, todoCarryover,
                 categories, hiddenCats, weekStart, theme, syncBackup,
                 feed, calcCollapsed, settingsGroups }
workingDays    [ "YYYY-MM-DD", ... ]
dataStart      "YYYY-MM-DD"
dataEnd        "YYYY-MM-DD"
```

予定の項目:

| 項目 | 内容 |
|---|---|
| `text` | タイトル |
| `color` | 色（6種。`settings.categories` の色と完全には対応しない） |
| `start` / `end` | `HH:MM`。null なら終日。時刻があれば必ず両方ある |
| `endDate` | 複数日予定の終了日。単日なら null |
| `location` / `note` | 場所・メモ |
| `uid` | ローカルの識別子（15文字） |
| `updatedAt` | 更新時刻（epoch ミリ秒） |
| `gcalId` / `calId` / `gUpdated` | Google Calendar の同期情報 |
| `todo` / `done` | ToDo フラグ。**タスクへ変換する** |
| `gtaskId` / `gtaskListId` / `gtaskUpdated` | Google Tasks の同期情報 |
| `src` | 取り込み元（`import`） |

繰り返し予定は上記に加えて `rule` / `from` / `until` / `except` / `dirty` を持つ。
`rule` は `{ "type": "yearly", "month": 4, "day": 21 }` の形。

### 引き継げないもの

**同期トークン（syncToken）がエクスポートに含まれていない。** 各予定に `gcalId` があるので
再リンクはできるが、差分同期の再開はできないため、移行後の初回は全再同期になる
（要件書 8 章の「引き継げない場合は初回に全再同期を走らせる」に該当）。

マイルストーンも含まれていない。`ツール用実働日.xlsx` から取り込み直す。
