# 実行計画: タスクトレイ状態表示とWindows通知の実装

## Goal
- `TrayService` のプレースホルダーを置き換え、同期状態（待機/同期中/エラー停止）をタスクトレイで判別できるようにする。
- `NotificationService` のプレースホルダーを置き換え、同期失敗や停止時にWindows通知を発火できるようにする。
- `SyncOrchestrator` と接続し、状態遷移と通知が実運用で動くようにする。

## Design Check
- 判定: **必要**
- 理由: Windows連携の中核（Tray/Notification）を実装し、同期制御との連携ポイントが増えるため `docs/design/maui-blazor-architecture.md` の更新が必要。

## Task List
- [x] 1. `TrayService` をWindows実装へ更新する（トレイアイコン表示・状態別アイコン切替）。
- [x] 2. `NotificationService` をWindows通知実装へ更新する（失敗時のフォールバック含む）。
- [x] 3. `SyncOrchestrator` にトレイ状態更新と通知呼び出しを組み込む。
- [x] 4. 必要なDI/プロジェクト設定を更新する（Windows Forms利用など）。
- [x] 5. `docs/design/maui-blazor-architecture.md` を更新し、ビルドで検証する。
- [x] 6. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Infrastructure/Services/TrayService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/NotificationService.cs`
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/archive/2026/02/20260206-tray-toast-implementation.md`

## Risks
- 実行環境によってはWindows通知API登録に失敗し、フォールバック経路のみが使われる可能性がある。
- トレイアイコン実装でUIスレッド境界を誤ると表示更新が不安定になる可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー指示「進めて」に基づき、タスクトレイ表示とWindows通知の実装を開始。
- 2026-02-06: `TrayService` を Win32 `Shell_NotifyIcon` ベースへ実装し、状態に応じたアイコン/ツールチップ更新とエラー時バルーン表示を追加。
- 2026-02-06: `NotificationService` を `AppNotificationManager` 実装へ更新し、短時間の重複通知抑制と失敗時ログフォールバックを追加。
- 2026-02-06: `SyncOrchestrator` に通知発火・エラーログ追記・トレイ状態更新の連携を追加。
- 2026-02-06: Windows Forms 利用案は MAUI ビルド衝突（MC6000）で採用せず、Win32 API 方式へ切り替え。
- 2026-02-06: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` で 0 エラーを確認。
