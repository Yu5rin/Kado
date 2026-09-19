# Google 側の入力項目と、この実装との差分

Phase 4（Google 同期）の計画に使うための調査。**書き込みできる項目**を全部挙げ、
いまの実装と要件書の計画に載っているかどうかを並べた。

出典（2026年9月時点）

- [Calendar API — Events](https://developers.google.com/workspace/calendar/api/v3/reference/events)
- [Calendar API — CalendarList](https://developers.google.com/workspace/calendar/api/v3/reference/calendarList)
- [Tasks API — Tasks](https://developers.google.com/workspace/tasks/reference/rest/v1/tasks)

判定の意味

| 印 | 意味 |
|---|---|
| ✅ | 実装済み（列があり、編集画面から入る） |
| 🔸 | 列はあるが画面から編集しない、または一部だけ |
| 📋 | 要件書に記載があり、あとのフェーズで作る予定 |
| ❌ | **実装も計画も無い** |

---

## 予定（Calendar API の Events）

| Google の項目 | 内容 | いまの状態 |
|---|---|---|
| `summary` | タイトル | ✅ `title` |
| `description` | 説明 | ✅ `note` |
| `location` | 場所（自由文） | ✅ `location` |
| `start` / `end` | 開始・終了（日付または日時） | ✅ `date`／`end_date`／`start_time`／`end_time` |
| `recurrence` | 繰り返し（RRULE・EXDATE ほか） | ✅ `recurrence`。画面は4種＋カスタム保持 |
| `source` | 由来（URL とタイトル） | 🔸 `url` のみ。タイトルは持っていない |
| `colorId` | **予定ごとの色**（カレンダー色の上書き） | 🔸 `color` 列はあるが編集させない。既定はカレンダー色 |
| `reminders` | 事前通知（最大5件、method と分） | 📋 通知は Phase 6（要件書 5.5・7.5） |
| `status` | confirmed／tentative／cancelled | ❌ |
| `attendees` | **ゲスト**（出欠、任意／必須、主催者） | ❌ |
| `conferenceData` | Google Meet の作成とリンク | ❌ |
| `attachments` | 添付ファイル（Drive、最大25） | ❌ |
| `transparency` | 予定あり／予定なし（空き時間の扱い） | ❌ |
| `visibility` | 公開設定（default／public／private／confidential） | ❌ |
| `guestsCanModify` | ゲストが変更できるか | ❌ |
| `guestsCanInviteOthers` | ゲストが他人を招待できるか | ❌ |
| `guestsCanSeeOtherGuests` | ゲストが他のゲストを見られるか | ❌ |
| `eventType` | 種別（default／focusTime／outOfOffice／workingLocation／birthday） | ❌ |
| `focusTimeProperties` ほか | 上の種別ごとの設定（自動辞退など） | ❌ |
| `eventLabelId` | 予定のラベル | ❌ |
| `extendedProperties` | アプリ独自のメタデータ（private／shared） | ❌ |
| `sequence` | iCalendar の版番号 | ❌（同期の内部用） |
| `id` | 識別子（作成時に指定可） | 🔸 `google_event_id` で対応づけ |
| `organizer` | 主催者（import 時のみ変更可） | ❌ |
| `anyoneCanAddSelf` | 誰でも自分を追加できる（非推奨） | ❌（Google 側が非推奨） |

読み取り専用なので実装不要：`created` `updated` `etag` `htmlLink` `iCalUID` `kind`
`creator` `hangoutLink` `locked` `originalStartTime` `recurringEventId`
`endTimeUnspecified` `attendeesOmitted` `privateCopy`

---

## タスク（Tasks API の Tasks）

| Google の項目 | 内容 | いまの状態 |
|---|---|---|
| `title` | タイトル（1024文字まで） | ✅ `title` |
| `notes` | 詳細（8192文字まで） | ✅ `note` |
| `status` | needsAction／completed | ✅ `is_done` |
| `due` | 期限。**日付のみ**（時刻は捨てられる） | ✅ `due` |
| `completed` | **完了した日時** | ❌ 完了したことは持つが、いつ完了したかは持たない |
| `parent` | **サブタスクの親**（変更は `move` メソッド） | ❌ |
| `position` | **同じ階層での並び順**（変更は `move` メソッド） | ❌ 期限順で並べており、手で並べ替えられない |
| `deleted` | 削除フラグ | 🔸 tombstone で対応 |
| `hidden` | 完了タスクを隠すか | ❌ |

読み取り専用：`kind` `id` `etag` `updated` `selfLink` `links` `webViewLink` `assignmentInfo`

### Tasks API に**無い**もの

Google ToDo リストの画面にはあるのに、API が公開していないため同期できない。

- **時刻つきの期限。** 画面では時刻を設定できるが、API の `due` は日付だけで、
  時刻は捨てられると明記されている
- **繰り返し。** 画面では繰り返しを設定できるが、API には項目が無い

どちらも「アプリで設定しても Google に送れない」「Google で設定しても読めない」ことになる。
**こちらの画面にも置かない**のが筋。置くと、同期したときに黙って消える。

---

## カレンダー一覧（CalendarList）

| Google の項目 | 内容 | いまの状態 |
|---|---|---|
| `backgroundColor` / `foregroundColor` | **カレンダーの色** | 🔸 名前から色を割り当てている。取り込んだら本物に差し替える |
| `selected` | 一覧で表示するか | ✅ 左パネルのチェック（ローカル保持） |
| `summaryOverride` | 表示名の上書き | ❌ |
| `defaultReminders` | そのカレンダーの既定通知 | 📋 通知は Phase 6 |
| `notificationSettings` | メール通知の設定 | ❌ |
| `hidden` | 一覧から隠す | ❌ |

**色はカレンダー単位が既定**で、予定ごとに `colorId` で上書きできる。いまの実装
（カレンダーの色を使い、予定ごとには選ばせない）は既定の考え方と一致している。

---

## Phase 4 に向けて手を打つべきもの

### まず前提：書き戻しは `patch` で行う

`events.patch` は**指定しなかった項目をそのまま残す**。公式の記載は
「Fields that you don't specify in the request remain unchanged.」。

つまり、こちらが扱う項目（タイトル・日時・場所・説明・URL・繰り返し）だけを送れば、
`attendees` も `reminders` も `conferenceData` も消えない。**列で抱え込む必要はない。**

ただし配列は例外で、「指定したら既存の配列を丸ごと置き換える」。`attendees` を
うっかり空配列で送ると招待が消える。**扱わない項目は送らない**を徹底する。

`tasks.patch` も同じ。

### そのうえで足したもの（スキーマ v3）

| 追加したもの | なぜ要るか |
|---|---|
| `events.google_raw` / `tasks.google_raw` | 最後に受け取った姿（キーをソートした JSON）。差分の誤検知を防ぐ（要件書 6.3） |
| `events.status` | `cancelled` が取り消しとして流れてくる。削除として扱う |
| `events.source_title` | `source` は url と title の対。url だけでは書き戻せない |
| `tasks.completed_at` | 完了したことだけでは、ローカルと Google のどちらが新しいか判定できない |
| `tasks.parent_id` / `tasks.position` | サブタスクと手動の並び順。Google 側では `move` でしか変えられないので読んで持つ |
| `calendars` 表 | 複数カレンダーの取得・表示 ON/OFF・並び順（要件書 6.3）。名前と色も本物になる |
| `task_lists` 表 | 同上。カレンダーとは独立した同期経路を持つ |

表示の ON/OFF は取り込みの Upsert で上書きしない。同期のたびにチェックが戻ると
使い物にならない（要件書 5.5）。

## 今回は入れないと決めたもの

- **ゲスト・Meet・添付の編集画面。** 生産計画の予定にゲストを足す運用が無いため。
  保持はするが編集はしない
- **`eventType`（focusTime など）と `eventLabelId`。** Google Workspace の機能で、
  個人アカウントでは使えない
- **Tasks の時刻つき期限と繰り返し。** API が公開しておらず、同期できないため
