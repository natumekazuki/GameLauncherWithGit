# 監視・デバウンス同期の実装計画

## Goal
- ファイル変更検知からリポジトリ単位のデバウンス同期キュー投入までを動作させる。
- アプリ起動時に登録済みゲームの関連リポジトリ監視を開始できる状態にする。

## Task List
- [x] `RepositoryWatcherService` をプレースホルダー実装から実体化する（FileSystemWatcherで変更通知）。
- [x] `SyncOrchestrator` にリポジトリ単位デバウンス（10秒）と単一実行制御を実装する。
- [x] `Home` 初期化時/保存後に監視対象を再構成する。
- [x] 設計ドキュメントを更新し、ビルド確認を行う。

## Affected Files
- `src/GameLauncherWithGit/Infrastructure/Services/RepositoryWatcherService.cs`
- `src/GameLauncherWithGit/Application/Services/SyncOrchestrator.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-watcher-debounce-sync.md`

## Risks
- FileSystemWatcherの重複イベントにより過剰キュー投入が発生する可能性。
- 監視対象再構成時の登録/解除漏れで不要監視が残る可能性。
- UI起点での初期化に依存するため、将来的な常駐モードでは初期化ポイント見直しが必要。

## Design Check
- 本対応は同期制御ロジック変更を含むため、`docs/design/maui-blazor-architecture.md` の更新を必須とする。

## Notes / Logs
- `dotnet build GameLauncherWithGit.sln -f net9.0-windows10.0.19041.0` でビルド成功（警告0/エラー0）。
