# SlideinaCalendar.Data

SQLite リポジトリ、マイグレーション、バックアップ/復元を担当する層。

**Phase 2 で実装する。** 現時点では空のプロジェクト。

予定している責務は要件書 3 章・8 章を参照。

- `%LOCALAPPDATA%\SlideinaCalendar\data.db`（Microsoft.Data.Sqlite + Dapper）
- 予定・タスク・マイルストーンを別テーブルで保持する
- 検索は FTS5
- 旧バックアップ JSON のインポート（ToDo フラグ → タスク変換を含む）
