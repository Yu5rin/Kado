# SlideinaCalendar.Google

Google Calendar / Google Tasks の同期、OAuth、トークン保護を担当する層。

**Phase 4 で実装する。** 現時点では空のプロジェクト。

予定している責務は要件書 6 章を参照。

- デスクトップアプリ型 OAuth クライアント（ループバックリダイレクト）
- 差分同期（syncToken）と全再同期の使い分け、tombstone による削除伝播
- 再リンク、重複検出・除去、JSON 正規化による差分誤検知の防止
- Calendar と Tasks は独立した同期経路にする
