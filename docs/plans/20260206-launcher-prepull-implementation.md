# 実行計画: LauncherService 起動前同期の本実装

## Goal
- `LauncherService` のプレースホルダー実装を置き換え、ゲーム起動前に関連リポジトリへ `git fetch` と `git pull --rebase` を実行する。
- 失敗時はゲーム起動を中止し、状態を `Error` に更新する。

## Design Check
- 判定: **必要**
- 理由: 起動フローの実ロジック追加（同期成功時のみ起動）に該当し、`docs/design/maui-blazor-architecture.md` の実装ステータス更新が必要。

## Task List
- [x] 1. モデル/サービス契約を拡張し、ゲームごとの関連リポジトリ情報を取得できるようにする。
- [x] 2. `Infrastructure/Services/GitService.cs` を実装し、`git` コマンドを実行して結果（exit code/stdout/stderr）を返す。
- [ ] 3. `Application/Services/LauncherService.cs` を実装し、`fetch -> pull --rebase` 成功時のみ exe を起動する。
- [ ] 4. `Home.razor` の起動結果メッセージを本実装に合わせて調整する（失敗理由を表示）。
- [ ] 5. ビルドで動作確認し、`docs/design/maui-blazor-architecture.md` の実装ステータスを更新する。
- [ ] 6. 計画ファイルを完了状態に更新し、`docs/plans/archive/2026/02/` へ移動する。

## Affected Files
- `src/GameLauncherWithGit/Application/Models/GameCardItem.cs`
- `src/GameLauncherWithGit/Application/Abstractions/IGameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/GitService.cs`
- `src/GameLauncherWithGit/Application/Services/LauncherService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260206-launcher-prepull-implementation.md`

## Risks
- 開発用シードデータの `ExecutablePath` / リポジトリパスが実在しない場合、起動結果は失敗になる。
- `git` 未インストール環境では `GitService` が常に失敗し、起動ブロックが発生する。
- 長時間同期中の UI フィードバックが不足すると、ユーザーがハングと誤認する可能性がある。

## Notes / Logs
- 2026-02-06: ユーザー指定スコープとして「LauncherService 本実装（起動前同期）」を選択。
- 2026-02-06: `GameCardItem` に `RelatedRepositoryPaths` を追加し、`IGameLibraryService` に `FindByIdAsync` を追加。
- 2026-02-06: `GitService` を実装し、`ProcessStartInfo` 経由で `git` 実行・キャンセル処理・標準出力/標準エラー収集を追加。
