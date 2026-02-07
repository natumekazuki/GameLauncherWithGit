# 目的
- Home のゲームカードから削除できるようにし、誤操作防止の確認導線を追加する。
- 削除時に SQLite データ、サムネイルファイル、監視対象の整合を保つ。

# Design Check
- 判定: 必須（UI操作追加 + データ削除ロジック追加）
- 対象: `docs/design/maui-blazor-architecture.md`
- 更新方針:
  - `GameLibraryService` の責務に削除処理を追記
  - Home のカード操作に削除導線を追記

# タスクリスト
- [ ] `IGameLibraryStore` / `SqliteGameLibraryStore` に削除APIを追加
- [ ] `IGameLibraryService` / `GameLibraryService` にゲーム削除処理を追加（サムネイル削除含む）
- [ ] `Home.razor` に削除ボタンと確認モーダルを追加
- [ ] 削除後の一覧再読込・監視再構成・通知表示を実装
- [ ] `docs/design/maui-blazor-architecture.md` を実装内容へ同期
- [ ] Windowsターゲットでビルド検証

# 変更対象ファイル
- `src/GameLauncherWithGit/Infrastructure/Abstractions/IGameLibraryStore.cs`
- `src/GameLauncherWithGit/Infrastructure/Services/SqliteGameLibraryStore.cs`
- `src/GameLauncherWithGit/Application/Abstractions/IGameLibraryService.cs`
- `src/GameLauncherWithGit/Application/Services/GameLibraryService.cs`
- `src/GameLauncherWithGit/Components/Pages/Home.razor`
- `docs/design/maui-blazor-architecture.md`
- `docs/plans/20260207-game-delete-feature.md`

# リスク
- 使用中ゲームの削除によるユーザー混乱
- サムネイル削除失敗で孤児ファイルが残る可能性
- 削除対象の競合更新時に UI 状態が崩れる可能性
