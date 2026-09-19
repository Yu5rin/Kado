# SlideinaCalendar.Shell

AppBar、ホットゾーン、トレイ、通知といった Win32 連携を集約する層。

**Phase 5 で実装する。** 現時点では空のプロジェクト。

成立性の検証は `samples/AppBarProbe` で先行して行う。
仕様と安全装置は要件書 2 章を正とする。

- `SHAppBarMessage`（`ABM_NEW` / `ABM_QUERYPOS` / `ABM_SETPOS` / `ABM_REMOVE`）
- `ABN_POSCHANGED` / `ABN_FULLSCREENAPP` の受信と再配置
- 異常終了時のワークエリア復旧（`SPI_SETWORKAREA`）
