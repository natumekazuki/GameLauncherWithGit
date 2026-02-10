# 目的
- `origin/main...HEAD` レビューで指摘した不具合（Critical/Warning/Suggestion）をすべて修正し、同期失敗の見逃しを防止する。

# Design Check
- 判定: 必須（同期ロジックの挙動変更）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - upstream 未設定時のみ pull/push をスキップすることを明確化
  - 追跡情報取得コマンド失敗時は同期失敗として扱う方針を追記

# タスクリスト
- [x] `SyncOrchestrator` で追跡情報取得失敗の扱いを修正（未設定と実行失敗を分離）
- [x] `LauncherService` で pull 対象解決失敗の扱いを修正（未設定と実行失敗を分離）
- [x] `LauncherService` の失敗メッセージに実行コマンドを反映
- [x] `docs/design/maui-blazor-architecture.md` を実装に同期
- [x] ビルド検証を実施し、結果を記録

# 変更対象ファイル
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260210-fix-origin-main-review-findings.md`

# リスク
- 失敗判定を厳格化することで、従来は継続していたケースがエラー停止に変わる可能性
- エラーメッセージ変更により既存ログ監視ルールの文字列一致条件へ影響する可能性

# Notes / Logs
- 実行コマンド: `dotnet build GameLauncherWithGit.sln -v minimal`
- 結果: 失敗（本タスク差分外の既知要因）
  - `src/GameLauncherWithGit/Infrastructure/Services/PathPickerService.cs:86` で `BuildErrorDetail` 未定義（CS0103）
  - `GameLauncherWithGit (PID 18780)` による DLL ロックで `MSB3021/MSB3027`
