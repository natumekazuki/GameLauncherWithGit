# 自動同期Gitパイプライン実装計画

## Goal
- 監視イベントから実際の Git 同期（`fetch -> pull --rebase -> add -A -> commit -> push`）を実行できる状態にする。
- 競合時は `ErrorPaused` に遷移し、それ以外の失敗は次回イベントで再試行可能な状態（`Idle`）に戻す。

## Task List
- [x] `SyncOrchestrator` の同期本体を実装する（git コマンド実行、結果判定、失敗分類）。
- [x] 監視キーをリポジトリパス単位に統一し、同一リポジトリの重複監視・重複同期を防ぐ。
- [x] 設計ドキュメントを更新する。
- [x] ビルド確認を行う。

## Affected Files
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-sync-git-pipeline.md`

## Risks
- pull/rebase 失敗判定の条件が不足すると、競合停止すべきケースを見逃す可能性。
- commit メッセージや `status --porcelain` 判定により、まれに「コミット不要」を誤判定する可能性。
- 同期処理が長時間化した場合、イベント再入の取り扱いで意図しない遅延が出る可能性。

## Design Check
- 同期フロー仕様変更のため、`docs/design/maui-blazor-architecture.md` 更新を必須とする。

## Notes / Logs
- `SyncOrchestrator` のプレースホルダー処理を実 Git 同期フローへ置換した。
- pull/rebase 競合を検知した場合のみ `ErrorPaused` へ遷移し、それ以外の同期失敗は `Idle` へ戻すようにした。
- `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` でビルド成功（警告0/エラー0）。
