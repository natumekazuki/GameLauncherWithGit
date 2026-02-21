# 目的
- 監視同期の失敗時に指数バックオフ再試行を導入し、一時的な通信障害で自動同期が止まり続けないようにする。
- アプリ内エラーログを `library/MonochromeMemory.Log` ベースへ切り替え、ログ出力を構造化する。

# Design Check
- 判定: 必須（ロジック変更 + 新規ログ基盤導入）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - 同期フローに「再試行スケジュール」と「通知抑制/復旧通知」を追記
  - ログ責務を `MonochromeMemory.Log` ベースへ更新

# タスクリスト
- [x] `SyncOrchestrator` に指数バックオフ再試行を実装（初回失敗通知 + 連続失敗通知抑制 + 復旧通知）
- [x] `SyncOrchestrator` の失敗状態管理を更新（`ErrorPaused` と一時失敗再試行の切り分け）
- [x] `MonochromeMemory.Log` / `MonochromeMemory.Log.Sinks.File` を `GameLauncherWithGit` に参照追加
- [x] `MauiProgram` で `MonochromeMemory.Log` のDI登録とファイルSink設定を追加
- [x] `LogAccessService` を `MonochromeMemory.Log` 出力へ更新（既存UI導線は維持）
- [x] `docs/design/maui-blazor-architecture.md` を実装内容に同期
- [x] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/LogAccessService.cs`
- `src/GameLauncherWithGit/MauiProgram.cs`
- `src/GameLauncherWithGit/GameLauncherWithGit.csproj`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-sync-backoff-monochrome-log.md`

# リスク
- 再試行制御の競合で同一リポジトリ同期が二重実行される可能性
- 連続失敗通知抑制の条件ミスで必要通知まで欠落する可能性
- 新ログ形式(JSONL)により既存運用の目視確認手順が変わる可能性

# Notes / Logs
- 実行コマンド: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false`
- 結果: ビルド成功（0 warnings / 0 errors）
- 2026-02-21: 回帰確認として `SyncOrchestrator` のバックオフ再試行ロジック、`LogAccessService` と `MauiProgram` の `MonochromeMemory.Log` 連携箇所を静的確認。
- 2026-02-21: `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0 -p:UseAppHost=false` 成功（0 warnings / 0 errors）。
