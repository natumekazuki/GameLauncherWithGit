# Plan: 同期履歴タイムライン実装

## Goal
- 各ゲームカードで「関連リポジトリの直近同期履歴（成功/失敗、時刻、所要時間、失敗理由）」を確認できるようにする。
- 再起動後も履歴が残るよう、SQLite（`game-library.db`）へ永続化する。

## Design Doc Check
- 本対応はデータモデル追加（同期履歴テーブル）とUI表示仕様の追加を含むため、`docs/design/maui-blazor-architecture.md` の更新が必須。

## Task List
- [x] 同期履歴のデータモデルを定義する（`RepositoryId`、`Status`、`StartedAt`、`FinishedAt`、`DurationMs`、`Command`、`Reason` など）。
- [x] `IGameLibraryStore` とは分離した同期履歴ストア抽象（例: `IRepositorySyncHistoryStore`）を追加する。
- [x] SQLite 実装に同期履歴テーブル作成とCRUD（最新N件取得、追加、古い履歴削除）を実装する。
- [x] `SyncOrchestrator` に履歴記録ポイントを追加する（成功時、再試行失敗時、ErrorPaused遷移時）。
- [x] `Home.razor` のカードUIに「同期履歴タイムライン」表示領域を追加する（直近3件程度）。
- [x] タイムライン表示用の整形ロジックを実装する（時刻表示、所要時間表示、失敗理由の短縮）。
- [x] 必要なCSSを追加し、既存カードUI（サムネイルあり/なし）と干渉しないことを確認する。
- [x] `docs/design/maui-blazor-architecture.md` を更新し、同期履歴データフローと表示仕様を同期する。
- [ ] ビルドと手動確認を実施する（同期成功/失敗を1件ずつ発生させ、カードに履歴が反映されること）。

## Affected Files
- `src/GameLauncherWithGit/Application/Abstractions/*.cs`（履歴ストア抽象）
- `src/GameLauncherWithGit/Application/Models/*.cs`（履歴モデル）
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Infrastructure/Abstractions/*.cs`（必要に応じて）
- `src/GameLauncherWithGit/Infrastructure/Services/Sqlite*.cs`（SQLite履歴実装）
- `src/GameLauncherWithGit/MauiProgram.cs`（DI登録）
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `src/GameLauncherWithGit/wwwroot/css/app.css`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-sync-history-timeline.md`

## Risks
- 同期イベントの記録タイミングが不適切だと、履歴の時刻や状態が実態とずれる可能性がある。
- 履歴を無制限で保持するとSQLite肥大化のリスクがあるため、保存上限の設計が必要。
- カード内情報量が増え、モバイル幅で可読性が下がる可能性がある。

## Notes / Logs
- 2026-02-07: 既存の `RepositoryStateStore` は現在状態のみ保持し、履歴は保持していない。
- 2026-02-07: 既存ログビューア強化は完了済みのため、今回は「カード単位で素早く見る同期履歴」にフォーカスする。
- 2026-02-07: `SqliteGameLibraryStore` に `RepositorySyncHistory` テーブルを追加し、`IRepositorySyncHistoryStore` の `AppendAsync` / `GetLatestByRepositoryIdsAsync` を実装。1リポジトリあたり最新50件保持に制限。
- 2026-02-07: `SyncOrchestrator` に同期履歴記録を追加（成功、失敗、ErrorPaused）。履歴記録失敗は同期処理を止めず警告ログで継続。
- 2026-02-07: Homeカードに同期履歴（直近3件）を追加。5秒周期で履歴を再取得し、時刻・所要時間・command・reason を表示。
- 2026-02-07: `dotnet build ... -p:OutDir=...` でビルド成功を確認。手動確認（成功/失敗の実発生）は未実施。
